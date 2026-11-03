using System.Text;
using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.StdValidators;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// <c>artifact.validate</c> (docs/04 section 10.3) through the engine's actions, as the workflow calls them: subjects from
/// what the step consumes, the repositories frozen at their work branch heads, the run tree's index and ledger, the reports
/// kept as the step's artifacts, and an intervention where the check cannot be made.
/// </summary>
public sealed class ValidationActionTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string RunId = "run-ticket";
    const string Branch = "ff/ticket/T-1";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-validate-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = new();
    readonly RepoRef repo;
    readonly RepoRef other;
    readonly FileSystemArtifactStore store;
    readonly InMemoryRecordStore records = new();
    readonly InMemoryValidators memory = new();
    readonly EngineActions actions;
    readonly List<ArtifactEntry> artifacts = [];
    Dictionary<string, string>? consumed;
    readonly List<ValidationRequest> seen = [];

    public ValidationActionTests()
    {
        Directory.CreateDirectory(folder);
        store = new FileSystemArtifactStore(folder);
        repo = forge.CreateRepository("acme/api");
        other = forge.CreateRepository("acme/web");
        memory.Add(new ValidatorDescriptor("spy", "Records its request", [], new Dictionary<string, string>(), []), r =>
        {
            seen.Add(r);
            return ValidationResult.Of([]);
        });
        var std = new StdValidators(() => store, () => new InMemoryCodeHost(forge), () => new InMemoryVcs(forge));
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = new Dictionary<string, IProvider> { ["one"] = new InMemoryVcs(forge) },
            ["code_host"] = new Dictionary<string, IProvider> { ["one"] = new InMemoryCodeHost(forge) },
            ["artifact_store"] = new Dictionary<string, IProvider> { ["one"] = store },
            ["record_store"] = new Dictionary<string, IProvider> { ["one"] = records },
            ["validator"] = new Dictionary<string, IProvider> { ["std"] = std, ["memory"] = memory, ["flaky"] = new Unreachable() },
        };
        var defaults = instances.Keys.ToDictionary(k => k, k => k == "validator" ? "std" : "one");
        actions = new EngineActions(new ProviderSet(instances, defaults, new Dictionary<string, string>()));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    string Put(string text) => store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Ct).GetAwaiter().GetResult().Unwrap().Id;

    async Task<string> ReadAsync(string reference)
    {
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), Ct)).Unwrap();
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync(Ct);
    }

    /// <summary>An artifact an earlier step of this run produced: by default an agent's.</summary>
    string Produce(string step, string name, string type, string text, string? action = null, bool evidence = false)
    {
        var reference = Put(text);
        artifacts.Add(new ArtifactEntry(step, name, "std:artifact_types/" + type, reference, action, evidence));
        return reference;
    }

    string Work(string path = "src/Widgets.cs", string content = "class Widgets { }\n", RepoRef? at = null) =>
        forge.Commit(at ?? repo, "refs/heads/" + Branch, new Dictionary<string, string?> { [path] = content }, "work");

    /// <summary>Runs <c>artifact.validate</c> with <paramref name="validation"/> (YAML), keeping <paramref name="produces"/> on both outcomes, as std now declares.</summary>
    Task<ActionResult> ValidateAsync(string validation, params string[] produces) => ValidateAtAsync("validate", validation, produces);

    Task<ActionResult> ValidateAtAsync(string stepId, string validation, params string[] produces)
    {
        var inputs = (Dictionary<string, object?>)YamlWriter.ReadPlain("validation:\n" + validation)!;
        var contract = new StepContract([new OutcomeSpec("valid", "success"), new OutcomeSpec("invalid", "failure")], new Dictionary<string, string>(),
            produces.SelectMany(p => new[] { new ArtifactSpec("valid", p), new ArtifactSpec("invalid", p) }).ToList(), new Dictionary<string, string>());
        var call = new StepCall(new StepIdentity(RunId, "ticket_delivery", stepId, 1, $"{RunId}/{stepId}/1"), "action", "artifact.validate",
            inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)), contract, false, null);
        var scope = new ActionScope([repo.FullName, other.FullName], repo.FullName, Branch, null, artifacts.ToList(), [], [], consumed);
        return actions.RunAsync(call, scope, () => { }, Ct);
    }

    async Task<ValidationReport> ReportAsync(ActionResult result, string name)
    {
        Assert.True(result.Report is not null, result.Reason);
        return JsonSerializer.Deserialize<ValidationReport>(await ReadAsync(result.Report.Artifacts[name]), ArtifactShapes.Engine)!;
    }

    const string Requirements = """
        schema: 1
        scope: { kind: ticket, id: T-1 }
        requirements:
          - { id: R-1, title: Names are short, behaviour: A long name is refused., supports_reason: The ticket }
        """;

    static string Plan(bool method = true) => """
        schema: 1
        scope: { kind: ticket, id: T-1 }
        acceptance_criteria:
          - id: AC-1
            title: Long names are refused
            behaviour: A 101-character name returns 400.
            supports: [{ artifact: requirements, requirement: R-1 }]
        """ + (method ? "\n    verification: [{ id: unit, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required }]" : "");

    /// <summary>std's ticket_delivery plan check (plan_and_validate's level_validators), parent plan optional.</summary>
    const string TicketValidators = """
          - validator: requirement_support
            subjects: [requirements, delivery_plan, { name: requirements, from: parent, optional: true }]
            options: { require_coverage: true }
          - validator: requirement_inheritance
            subjects: [{ name: child_requirements, from: parent, optional: true }, delivery_plan]
            options: { allow_rewording: true }
          - validator: verification_method_support
            subjects: [delivery_plan]
        """;

    [Fact]
    public async Task A_standalone_tickets_plan_is_checked_by_the_standard_validators_against_the_frozen_head()
    {
        var head = Work();
        Produce("plan", "requirements", "requirements", Requirements);
        Produce("plan", "delivery_plan", "delivery_plan", Plan());

        var result = await ValidateAsync(TicketValidators, "plan_validation");

        Assert.Equal("valid", result.Report?.Outcome);
        var report = await ReportAsync(result, "plan_validation");
        Assert.True(report.Valid);
        Assert.Equal(["requirement_support", "requirement_inheritance", "verification_method_support"], report.Validations.Select(v => v.Validator));
        Assert.All(report.Validations, v => Assert.Equal("std", v.Provider));
        // acme/web has no work branch: nothing was prepared there, so nothing is frozen.
        Assert.Equal(new Dictionary<string, string> { ["acme/api"] = head }, report.Repositories);
        // The parent's plan is absent, passed as absent (LOG 63), so requirement_inheritance passes for a standalone ticket.
        var inheritance = report.Validations[1].Subjects;
        Assert.True(inheritance[0].Absent);
        Assert.Equal(("child_requirements", "parent"), (inheritance[0].Name, inheritance[0].From));
    }

    [Fact]
    public async Task An_invalid_plan_is_invalid_and_its_report_is_kept_for_the_planner()
    {
        Work();
        Produce("plan", "requirements", "requirements", Requirements);
        Produce("plan", "delivery_plan", "delivery_plan", Plan(method: false));

        var result = await ValidateAsync(TicketValidators, "plan_validation");

        Assert.Equal("invalid", result.Report?.Outcome);
        var report = await ReportAsync(result, "plan_validation");
        Assert.False(report.Valid);
        var issue = Assert.Single(report.Validations.SelectMany(v => v.Result.Issues));
        Assert.Equal(("no_method", "AC-1"), (issue.Code, issue.Item));
    }

    [Fact]
    public async Task A_required_subject_that_does_not_exist_or_cannot_be_read_stops_the_step_for_a_person()
    {
        Work();
        var missing = await ValidateAsync("  - { validator: verification_method_support, subjects: [delivery_plan] }\n", "plan_validation");
        Assert.Equal(StepOutcome.CannotAnswer, missing.Kind);
        Assert.Contains("subject delivery_plan does not exist, and is not optional", missing.Reason);
        // The map form is required unless it says optional.
        var mapped = await ValidateAsync("  - { validator: verification_method_support, subjects: [{ name: delivery_plan }] }\n", "plan_validation");
        Assert.Equal(StepOutcome.CannotAnswer, mapped.Kind);
        Assert.Contains("subject delivery_plan does not exist, and is not optional", mapped.Reason);

        artifacts.Add(new ArtifactEntry("plan", "delivery_plan", "std:artifact_types/delivery_plan", "sha256:" + new string('0', 64)));
        var unreadable = await ValidateAsync("  - { validator: verification_method_support, subjects: [delivery_plan] }\n", "plan_validation");
        Assert.Equal(StepOutcome.CannotAnswer, unreadable.Kind);
        Assert.Contains("cannot be read", unreadable.Reason);
    }

    [Theory]
    [InlineData("  - { validator: no_such_validator, subjects: [] }\n", "no validator provider offers no_such_validator")]
    [InlineData("  - { validator: verification_method_support, subjects: [delivery_plan], options: { strict: true } }\n", "has no option 'strict'")]
    [InlineData("  - { validator: verification_method_support, subjects: [requirements] }\n", "does not take requirements")]
    [InlineData("  - { validator: verification_method_support, subjects: [{ name: delivery_plan, from: sideways }] }\n", "comes from current, parent, root")]
    [InlineData("  - { subjects: [delivery_plan] }\n", "with a 'validator' name")]
    public async Task A_validator_no_provider_offers_or_one_that_refuses_its_request_stops_the_step_for_a_person(string validation, string reason)
    {
        Work();
        Produce("plan", "requirements", "requirements", Requirements);
        Produce("plan", "delivery_plan", "delivery_plan", Plan());

        var result = await ValidateAsync(validation, "plan_validation");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains(reason, result.Reason);
    }

    [Fact]
    public async Task A_validator_is_found_in_any_validator_provider_and_gets_the_run_trees_index_and_ledger_and_subjects_from_other_runs()
    {
        Work();
        Work("app.ts", "x", other);
        // The parent run and a child of this run, in the record store with the artifacts they produced and a finding.
        var parentPlan = Put(Requirements);
        var childClaims = Put("schema: 1\nclaims: []\n");
        await records.AppendAsync(new RecordBatch(
            Runs:
            [
                new RunRecord("run-epic", null, "epic_delivery", "running", "someone", ["acme/api"], false, DateTimeOffset.UnixEpoch, null),
                new RunRecord(RunId, "run-epic", "ticket_delivery", "running", "someone", ["acme/api"], false, DateTimeOffset.UnixEpoch, null),
                new RunRecord("run-child", RunId, "ticket_delivery", "running", "someone", ["acme/api"], false, DateTimeOffset.UnixEpoch, null),
            ],
            Artifacts:
            [
                new ArtifactRecord("run-epic", "design", 1, "requirements", "std:artifact_types/requirements", parentPlan, null, false, DateTimeOffset.UnixEpoch),
                new ArtifactRecord("run-child", "implement", 1, "implementation_claims", "std:artifact_types/evidence_claims", childClaims, null, false, DateTimeOffset.UnixEpoch),
            ],
            Findings: [new FindingRecord("run-child", "F-1", "fp", new ReportedFinding("R", "t", "d", "high", true, "reasoned", new FindingLocation("acme/api", "a.cs")),
                "reviewer", null, "open", "abc", "abc", 0, 0, 0, false, DateTimeOffset.UnixEpoch)]), Ct);
        var own = Produce("plan", "delivery_plan", "delivery_plan", Plan());
        consumed = new Dictionary<string, string> { ["delivery_plan"] = own, ["parent/requirements"] = parentPlan };

        var result = await ValidateAsync("""
              - validator: spy
                subjects: [delivery_plan, { name: requirements, from: parent }, { name: contract, from: parent, optional: true }]
            """, "plan_validation");

        Assert.Equal("valid", result.Report?.Outcome);
        var request = Assert.Single(seen);
        Assert.Equal("memory", (await ReportAsync(result, "plan_validation")).Validations.Single().Provider);
        Assert.Equal(
            [("delivery_plan", "current", "std:artifact_types/delivery_plan", own), ("requirements", "parent", "std:artifact_types/requirements", parentPlan)],
            request.Subjects.Take(2).Select(s => (s.Name, s.From, s.Type!, s.Artifact!.Id)));
        Assert.True(request.Subjects[2].Absent);
        Assert.Equal(["acme/api", "acme/web"], request.Repositories.Keys.Order());
        // This run's artifacts as the workflow holds them, and the child's from the index; not the parent's.
        Assert.Equal([(RunId, "delivery_plan"), ("run-child", "implementation_claims")], request.Artifacts.Select(a => (a.RunId, a.Name)));
        Assert.Equal("F-1", Assert.Single(request.Findings).FindingId);
    }

    [Fact]
    public async Task In_a_child_run_a_subject_from_its_parent_the_workflow_did_not_resolve_stops_the_step_rather_than_passing_as_absent()
    {
        Work();
        Produce("plan", "delivery_plan", "delivery_plan", Plan());
        await records.AppendAsync(new RecordBatch(Runs:
        [
            new RunRecord("run-epic", null, "epic_delivery", "running", "someone", ["acme/api"], false, DateTimeOffset.UnixEpoch, null),
            new RunRecord(RunId, "run-epic", "ticket_delivery", "running", "someone", ["acme/api"], false, DateTimeOffset.UnixEpoch, null),
        ]), Ct);

        var result = await ValidateAsync("""
              - validator: requirement_inheritance
                subjects: [{ name: child_requirements, from: parent, optional: true }, delivery_plan]
            """, "plan_validation");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("subject parent/child_requirements is from another run", result.Reason);
    }

    /// <summary>
    /// Without the workflow's consumed artifacts, a subject is the one an agent step at the same place would consume
    /// (review F3): a producer in the step's own scope first, then the first by step id. Here verify.satisfaction sees the
    /// fixer's claims (verify.fix.fix), as the repair agent beside it does, not implement's or verify.repair's.
    /// </summary>
    [Fact]
    public async Task A_subject_with_several_producers_is_the_one_an_agent_step_beside_it_would_consume()
    {
        Work();
        Produce("implement", "implementation_claims", "evidence_claims", "schema: 1\nclaims: []\n# implement\n");
        var fixer = Produce("verify.fix.fix", "implementation_claims", "evidence_claims", "schema: 1\nclaims: []\n# fix\n");
        Produce("verify.repair", "implementation_claims", "evidence_claims", "schema: 1\nclaims: []\n# repair\n");

        var result = await ValidateAtAsync("verify.satisfaction", "  - { validator: spy, subjects: [implementation_claims] }\n", "verification_report");

        Assert.Equal("valid", result.Report?.Outcome);
        Assert.Equal(fixer, Assert.Single(seen).Subjects.Single().Artifact!.Id);
    }

    [Fact]
    public async Task A_set_made_by_artifact_compose_is_passed_with_its_members()
    {
        Work();
        var plan = Produce("plan", "delivery_plan", "delivery_plan", Plan());
        var requirements = Produce("plan", "requirements", "requirements", Requirements);
        var set = new ArtifactSet("spec", [new ArtifactSetMember("delivery_plan", "std:artifact_types/delivery_plan", plan),
            new ArtifactSetMember("requirements", "std:artifact_types/requirements", requirements)]);
        artifacts.Add(new ArtifactEntry("compose", "spec", null, Put(JsonSerializer.Serialize(set, ArtifactShapes.Engine)), "artifact.compose"));

        var result = await ValidateAsync("  - { validator: spy, subjects: [spec] }\n", "plan_validation");

        Assert.Equal("valid", result.Report?.Outcome);
        var subject = Assert.Single(Assert.Single(seen).Subjects);
        Assert.Equal([("delivery_plan", plan), ("requirements", requirements)], subject.Members!.Select(m => (m.Name, m.Artifact!.Id)));
    }

    [Fact]
    public async Task A_validators_infrastructure_error_is_retried_not_an_intervention()
    {
        Work();

        await Assert.ThrowsAsync<ActionInfrastructureException>(() => ValidateAsync("  - { validator: flaky, subjects: [] }\n", "plan_validation"));
    }

    /// <summary>A validator provider that is unreachable, as a remote one may be.</summary>
    sealed class Unreachable : IValidatorProvider
    {
        public ProviderDescription Describe() => new("validator", "unreachable", "0", new NoCapabilities());
        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
        public IReadOnlyList<ValidatorDescriptor> Validators() => [new("flaky", "Unreachable", [], new Dictionary<string, string>(), [])];

        public Task<ProviderResult<ValidationResult>> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<ValidationResult>.Failure("unreachable", "the validator service did not answer"));
    }

    [Fact]
    public async Task The_trace_report_is_kept_as_trace_report_and_the_validation_report_as_the_other_artifact()
    {
        var head = Work("tests/WidgetTests.cs", "RefusesLongNames\n");
        Produce("plan", "requirements", "requirements", Requirements);
        Produce("plan", "delivery_plan", "delivery_plan", Plan());
        Produce("implement", "implementation_claims", "evidence_claims", """
            schema: 1
            claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: WidgetTests.RefusesLongNames, change: new }] }]
            """);
        Produce("tests", "test_results", "test_results",
            JsonSerializer.Serialize(new TestEvidence("acme/api", head, false, true, [new TestResults(head, "unit", 1, 0, 0, [], ["WidgetTests.RefusesLongNames"])]), ArtifactShapes.Engine),
            "test.run", true);

        var result = await ValidateAsync("  - { validator: requirement_trace, subjects: [requirements] }\n", "trace_report", "trace_validation");

        Assert.Equal("valid", result.Report?.Outcome);
        var report = await ReportAsync(result, "trace_validation");
        Assert.Equal(result.Report!.Artifacts["trace_report"], report.Validations.Single().Result.Reports.Single().Artifact.Id);
        var trace = JsonSerializer.Deserialize<TraceReport>(await ReadAsync(result.Report.Artifacts["trace_report"]), ArtifactShapes.Engine)!;
        Assert.True(trace.Traced);
    }

    /// <summary>
    /// F7 at run time: an agent step cannot declare an evidence type (check 12), and even an artifact of an evidence type an
    /// agent made (named test_results, as if it wrote its own results file) is not evidence: only an engine test action's is.
    /// </summary>
    [Theory]
    [InlineData(null, false, "invalid")]
    [InlineData(null, true, "invalid")]
    [InlineData("test.run", true, "valid")]
    public async Task Only_test_results_an_engine_test_action_produced_satisfy_a_claim(string? action, bool evidence, string outcome)
    {
        var head = Work("tests/WidgetTests.cs", "RefusesLongNames\n");
        Produce("plan", "delivery_plan", "delivery_plan", Plan());
        Produce("implement", "implementation_claims", "evidence_claims", """
            schema: 1
            claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: WidgetTests.RefusesLongNames, change: new }] }]
            """);
        Produce("implement", "test_results", "test_results",
            JsonSerializer.Serialize(new TestEvidence("acme/api", head, false, true, [new TestResults(head, "unit", 1, 0, 0, [], ["WidgetTests.RefusesLongNames"])]), ArtifactShapes.Engine),
            action, evidence);

        var result = await ValidateAsync("  - { validator: verification_satisfaction, subjects: [delivery_plan, implementation_claims, test_results] }\n", "verification_report");

        Assert.Equal(outcome, result.Report?.Outcome);
        if (outcome == "invalid")
            Assert.Contains((await ReportAsync(result, "verification_report")).Validations.Single().Result.Issues, i => i.Code == "not_evidence");
    }
}
