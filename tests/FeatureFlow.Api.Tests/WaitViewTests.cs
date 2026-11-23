using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WaitView = FeatureFlow.Api.Contracts.WaitView;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// An API over SQLite records, files and <see cref="FakeTemporal"/>, with the std library and a small test library
/// (<c>a2t</c>), for the decision views of B1 (package a2). Its one caller is the local token's <c>tester</c>, who holds
/// every role. A run started through it has its lock and its first plan revision indexed, as the engine would.
/// </summary>
public sealed class DecisionWorld : IAsyncDisposable
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-a2-" + Guid.NewGuid().ToString("N"));
    readonly Microsoft.AspNetCore.Builder.WebApplication app;
    readonly LibrarySet libraries;

    /// <summary>The test library: a triage decision producing std's <c>triage</c>, after a <c>for_each</c> over review angles.</summary>
    static readonly Dictionary<string, string> TestLibrary = new()
    {
        ["library.yaml"] = """
            name: a2t
            version: 0.1.0
            description: Package a2's decision views
            requires:
              plan_schema: 1
            dependencies:
              - { name: std }
            """,
        ["prompts/triage.md"] = "# Triage the findings\n\nDecide each finding an agent may not clear.\n",
        ["templates/triage.yaml"] = """
            api_version: feature-flow/v1
            kind: template
            metadata:
              name: triage
              level: ticket
              description: Reviewers by angle, then a person triages
            artifact_types:
              - std:artifact_types/triage
            defaults:
              routes:
                by_class: { failure: { escalate: true }, blocked: { escalate: true } }
            steps:
              - id: review
                for_each:
                  items: [correctness, security]
                  step:
                    - id: assess
                      decision: { type: findings.triage, prompt: a2t:prompts/triage }
                      outcomes: { reviewed: success }
              - id: person_triage
                decision: { type: findings.triage, prompt: a2t:prompts/triage }
                produces:
                  triaged: [{ name: person_triage, type: std:artifact_types/triage }]
                outcomes: { triaged: success, escalate: blocked }
                routes:
                  triaged: { end: completed }
            """,
    };

    public DecisionWorld(bool validators = false)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        var library = Path.Combine(folder, "a2t");
        foreach (var (path, text) in TestLibrary)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(library, path))!);
            File.WriteAllText(Path.Combine(library, path), text);
        }

        Records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        Store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = Records },
            ["artifact_store"] = new Dictionary<string, IProvider> { ["fs"] = Store },
            ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
        };
        var defaults = new Dictionary<string, string> { ["record_store"] = "sqlite", ["artifact_store"] = "fs", ["secrets"] = "file" };
        if (validators)
        {
            instances["validator"] = new Dictionary<string, IProvider> { ["memory"] = Validators };
            defaults["validator"] = "memory";
        }

        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "tester", ["viewer", "approver", "steerer", "admin"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var std = Path.Combine(Repository.Root, "library", "std");
        var organisation = new OrganisationConfig("t",
            [new LibrarySourceConfig("std", "directory", std), new LibrarySourceConfig("a2t", "directory", library)],
            new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"));
        libraries = new LibrarySet(Library.Library.Open(std, new DiagnosticBag())!, Library.Library.Open(library, new DiagnosticBag())!);
        app = ApiApp.Build(new ApiServices(new ProviderSet(instances, defaults, new Dictionary<string, string>()), installation, organisation, Temporal.Client),
            "http://127.0.0.1:0");
        app.StartAsync(Ct).GetAwaiter().GetResult();
        Http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
    }

    public SqliteRecordStore Records { get; }

    public FileSystemArtifactStore Store { get; }

    public FakeTemporal Temporal { get; } = new();

    public InMemoryValidators Validators { get; } = new();

    public HttpClient Http { get; }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Starts <c>a2t:templates/triage</c> through the API (its lock and record), then indexes its first plan revision as the engine does.</summary>
    public async Task<RunRecord> StartAsync()
    {
        using (var started = await Http.PostAsync("runs", new StringContent("""{ "template": "a2t:templates/triage" }""", Encoding.UTF8, "application/json"), Ct))
            Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var run = Assert.Single((await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), Ct)).Unwrap());
        var (core, diagnostics) = RunCore.Start(libraries, LibraryReference.TryParse(run.Template)!, new RunInfo(run.RunId, [], new Dictionary<string, YNode>()));
        Assert.True(core is not null, string.Join("; ", diagnostics.Select(d => d.Message)));
        foreach (var revision in core!.TakeRevisions())
            await AppendAsync(new RecordBatch(Artifacts:
                [new ArtifactRecord(run.RunId, "", revision.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, await PutAsync(revision.Write()), null, false, DateTimeOffset.UtcNow)]));
        return run;
    }

    /// <summary>A run recorded directly, as the engine records one, with no lock or plan.</summary>
    public async Task<RunRecord> RunAsync(string runId, string? trackerIssue = null, string? trackerTitle = null)
    {
        var run = new RunRecord(runId, null, "std:templates/ticket_delivery", "running", "tester", ["acme/api"], false, DateTimeOffset.UtcNow, null,
            TrackerIssue: trackerIssue, TrackerTitle: trackerTitle);
        await AppendAsync(new RecordBatch([run]));
        return run;
    }

    public async Task AppendAsync(RecordBatch batch) => (await Records.AppendAsync(batch, Ct)).Unwrap();

    public async Task<string> PutAsync(string text) => (await Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Ct)).Unwrap().Id;

    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await Http.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<T>(body, ApiJson.Options)!;
    }

    public async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string body)
    {
        using var response = await Http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An answer as the API records one once the run replied: the person's arguments and the run's result.</summary>
    public static AnswerRecord Answered(string runId, AnswerArgs args, DateTimeOffset at, string result = "recorded") =>
        new(runId, args.WaitId, args.Actor, JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)), result, at, args.Reason);
}

/// <summary>
/// A wait's view beyond its record (design-v2 s4.5, A5, A11, A15): its title and one-line summary by kind, an intervention's
/// cause with the attempts people granted and whether a retry is likely to repeat, a merge's pull request, a patch, a
/// session, and an open gate's consumed artifacts with their type and <c>changes</c>.
/// </summary>
public sealed class WaitViewTests
{
    static CancellationToken Ct => DecisionWorld.Ct;

    static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    static WaitRecord Wait(string runId, string waitId, string kind, string stepId, string type = "", string prompt = "reason", bool open = true) =>
        new(runId, waitId, kind, stepId, type, prompt, [], open, T0);

    static async Task<WaitView> ViewAsync(DecisionWorld world, string runId, string waitId) =>
        (await world.GetAsync<RunDetail>($"runs/{runId}")).Waits.Single(w => w.WaitId == waitId);

    [Fact]
    public async Task Each_kind_of_wait_has_its_title_and_a_one_line_summary()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1", "ACME-12", "Add widgets");
        await world.AppendAsync(new RecordBatch(Waits:
        [
            Wait("r1", "exhausted", "intervention", "verify.fix", prompt: "attempts exhausted") with
            {
                Cause = new WaitCause("verify.fix", Actions.AttemptsExhausted, "attempts exhausted", "I tried", 3, 3,
                    [new SentBack(2, "verify.fix.record_fixes", "invalid", "record_fixes refused declined\nmore detail")]),
            },
            Wait("r1", "blocked", "intervention", "verify.tests", prompt: "blocked escalates: no tests") with
            {
                Cause = new WaitCause("verify.tests", "blocked", "blocked escalates: no tests", "no tests to run", 1, 3, []),
            },
            Wait("r1", "old", "intervention", "implement", prompt: "attempts exhausted"),
            Wait("r1", "patch", "patch_approval", "plan", prompt: "a patch") with
            {
                Patch = new WaitPatch("agent", "verify.fix", "split the step", ["adds a step"], "verify.fix", 1, false, "{}"),
            },
            Wait("r1", "pause", "breakpoint", "deliver.merge", prompt: "paused before deliver.merge"),
            Wait("r1", "ask", "question", "implement", prompt: "Which database?\nPostgres or SQLite"),
            Wait("r1", "spec", "gate", "approve_spec", "ticket.spec_approval", "# Approve the plan for 198\n\nThe planner has written the requirements."),
            Wait("r1", "other", "gate", "sign_off", "acme.sign_off", "Is it ready?"),
            Wait("r1", "merge", "gate", "deliver.merge", "ticket.merge_action", "# Merge?\n\nThe change is ready."),
        ]));

        var waits = (await world.GetAsync<RunDetail>("runs/r1")).Waits.ToDictionary(w => w.WaitId);

        Assert.Equal(("verify.fix has used all its attempts", "record_fixes refused declined"), (waits["exhausted"].Title, waits["exhausted"].Summary));
        Assert.Equal(("verify.tests needs you: blocked", "no tests to run"), (waits["blocked"].Title, waits["blocked"].Summary));
        Assert.Equal("implement has used all its attempts", waits["old"].Title);
        Assert.Equal(("verify.fix proposes a plan change", "split the step"), (waits["patch"].Title, waits["patch"].Summary));
        Assert.Equal("Paused before deliver.merge", waits["pause"].Title);
        Assert.Equal(("implement asks", "Which database?"), (waits["ask"].Title, waits["ask"].Summary));
        Assert.Equal(("Approve the plan for ACME-12 Add widgets", "The planner has written the requirements."), (waits["spec"].Title, waits["spec"].Summary));
        Assert.Equal(("sign_off waits for your decision", "Is it ready?"), (waits["other"].Title, waits["other"].Summary));
        // A merge with no pull request recorded falls back to its prompt's heading.
        Assert.Equal("Merge?", waits["merge"].Title);
    }

    [Fact]
    public async Task A_spec_approval_of_a_run_with_no_tracker_issue_is_titled_by_its_prompts_heading()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        await world.AppendAsync(new RecordBatch(Waits: [Wait("r1", "spec", "gate", "approve_spec", "ticket.spec_approval", "# Approve the plan for 198\n\ntext")]));

        Assert.Equal("Approve the plan for 198", (await ViewAsync(world, "r1", "spec")).Title);
    }

    [Fact]
    public async Task An_interventions_cause_carries_the_grants_given_its_step_and_whether_a_retry_is_likely_to_repeat()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        SentBack Refused(int execution) => new(execution, "verify.fix.record_fixes", "invalid", "record_fixes refused declined");
        await world.AppendAsync(new RecordBatch(
            Waits:
            [
                Wait("r1", "first", "intervention", "verify.fix", open: false),
                Wait("r1", "elsewhere", "intervention", "implement", open: false),
                Wait("r1", "now", "intervention", "verify.fix", prompt: "attempts exhausted") with
                {
                    Cause = new WaitCause("verify.fix", Actions.AttemptsExhausted, "attempts exhausted", null, 5, 5, [Refused(3), Refused(4)]),
                },
            ],
            Answers:
            [
                // At the earlier intervention alice granted 2 attempts: recorded as sent, then with the run's reply. The grant elsewhere is another step's.
                DecisionWorld.Answered("r1", new AnswerArgs("first", "alice", Action: Overrides.GrantAttempts, Attempts: 2, Reason: "one more go"), T0, "sent"),
                DecisionWorld.Answered("r1", new AnswerArgs("first", "alice", Action: Overrides.GrantAttempts, Attempts: 2, Reason: "one more go"), T0.AddMinutes(1)),
                DecisionWorld.Answered("r1", new AnswerArgs("elsewhere", "alice", Action: Overrides.GrantAttempts, Attempts: 4), T0.AddMinutes(2)),
            ],
            Overrides:
            [
                new OverrideRecord("r1", "o1", Overrides.GrantAttempts, "bob", "flaky", "verify.fix", new Dictionary<string, string> { ["attempts"] = "1" }, "sent", T0.AddMinutes(3)),
                new OverrideRecord("r1", "o1", Overrides.GrantAttempts, "bob", "flaky", "verify.fix", new Dictionary<string, string> { ["attempts"] = "1" }, "applied", T0.AddMinutes(3)),
                new OverrideRecord("r1", "o2", Overrides.GrantAttempts, "carol", null, "verify.fix", new Dictionary<string, string> { ["attempts"] = "9" }, "refused: no", T0.AddMinutes(4)),
            ]));

        var cause = (await ViewAsync(world, "r1", "now")).Cause!;

        Assert.Equal(("verify.fix", Actions.AttemptsExhausted, 5, 5L), (cause.StepId, cause.Outcome, cause.AttemptsUsed, cause.AttemptsAllowed));
        Assert.Equal([(2L, "alice", "one more go"), (1L, "bob", "flaky")], cause.Grants.Select(g => (g.Attempts, g.Actor, g.Reason)));
        Assert.Equal([3, 4], cause.SentBack.Select(s => s.Execution));
        Assert.True(cause.LikelyToRepeat);
    }

    [Fact]
    public async Task A_retry_is_not_likely_to_repeat_when_the_step_was_sent_back_for_different_reasons()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        await world.AppendAsync(new RecordBatch(Waits:
        [
            Wait("r1", "now", "intervention", "verify.fix") with
            {
                Cause = new WaitCause("verify.fix", Actions.AttemptsExhausted, "attempts exhausted", null, 2, 2,
                    [new SentBack(1, "verify.tests", "failed", "2 tests fail"), new SentBack(2, "verify.tests", "failed", "1 test fails")]),
            },
        ]));

        var cause = (await ViewAsync(world, "r1", "now")).Cause!;

        Assert.False(cause.LikelyToRepeat);
        Assert.Empty(cause.Grants);
    }

    [Fact]
    public async Task A_merge_wait_carries_the_pull_request_its_step_last_read_and_is_titled_by_it()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        PullRequestRecord Read(int number, string stepId, int minutes) =>
            new("r1", "acme/api", number, stepId, new PullRequestState(new PullRequestRef(new RepoRef("acme/api"), number), "open", $"PR {number}", "", "ff/ticket/r1",
                "abc123", "main", false, true, [], [], null, null, $"https://github.com/acme/api/pull/{number}", 10, 2, 1, "clean"),
                [new PullRequestFile("src/A.cs", "modified", 10, 2)], new CheckStatus("abc123", [new CheckRun("build", "completed", "success", null, "https://ci/1")]),
                T0.AddMinutes(minutes));
        await world.AppendAsync(new RecordBatch(
            Waits: [Wait("r1", "merge", "gate", "deliver.merge", "ticket.merge_action", "Merge?")],
            PullRequests: [Read(7, "deliver.open", 1), Read(8, "deliver.merge", 2)]));

        var view = await ViewAsync(world, "r1", "merge");

        Assert.Equal("Merge acme/api#8 into main?", view.Title);
        var pr = view.PullRequest!;
        Assert.Equal((8, "deliver.merge", 10, "clean"), (pr.Number, pr.StepId, pr.Additions, pr.MergeableState));
        Assert.Equal(["src/A.cs"], pr.Files.Select(f => f.Path));
        Assert.Equal([("build", "success")], pr.Checks.Select(c => (c.Name, c.Conclusion)));
    }

    [Fact]
    public async Task A_merge_wait_whose_step_read_no_pull_request_carries_the_runs_only_one()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        await world.AppendAsync(new RecordBatch(
            Waits: [Wait("r1", "merge", "gate", "deliver.merge", "ticket.merge_action", "Merge?")],
            PullRequests: [new PullRequestRecord("r1", "acme/api", 3, "deliver.open", new PullRequestState(new PullRequestRef(new RepoRef("acme/api"), 3), "open", "t", "",
                "h", "r", "develop", false, null, [], [], null, null, null), null, null, T0)]));

        var view = await ViewAsync(world, "r1", "merge");

        Assert.Equal(3, view.PullRequest!.Number);
        Assert.Equal("Merge acme/api#3 into develop?", view.Title);
    }

    [Fact]
    public async Task A_patch_approval_carries_its_patch_and_a_gate_with_a_session_carries_its_id()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1", "ACME-12");
        await world.AppendAsync(new RecordBatch(
            Waits:
            [
                Wait("r1", "patch", "patch_approval", "plan") with { Patch = new WaitPatch("human", null, "why", ["removes a step"], "implement", 2, true, "{ changes: [] }") },
                Wait("r1", "plan", "gate", "plan_session", "ticket.spec_approval", "# Plan"),
            ],
            Sessions:
            [
                new SessionRecord("s-old", "r1", "plan", "plan_session", "tester", "acme/api", "ff/s", "abc", SessionStatuses.Closed, null, null, T0),
                new SessionRecord("s-new", "r1", "plan", "plan_session", "tester", "acme/api", "ff/s", "abc", SessionStatuses.Prepared, null, null, T0.AddMinutes(1)),
            ]));

        var waits = (await world.GetAsync<RunDetail>("runs/r1")).Waits.ToDictionary(w => w.WaitId);

        var patch = waits["patch"].Patch!;
        Assert.Equal(("human", "implement", 2L, true, "{ changes: [] }"), (patch.ProposerType, patch.ContinuesAt, patch.BaseRevision, patch.FromChild, patch.Document));
        Assert.Equal("plan proposes a plan change", waits["patch"].Title);
        Assert.Equal(("s-new", "Your planning session for ACME-12"), (waits["plan"].SessionId, waits["plan"].Title));
        Assert.Null(waits["patch"].SessionId);
    }

    [Fact]
    public async Task A_triage_is_titled_by_how_many_findings_need_a_person()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        FindingRecord Finding(string id, string state, bool blocking) =>
            new("r1", id, id, new ReportedFinding("r", id, "d", "high", blocking, FindingBases.Reasoned, new FindingLocation("acme/api", "A.cs")), "review", null, state,
                "h1", "h1", 0, 0, 0, false, T0);
        await world.AppendAsync(new RecordBatch(
            Waits: [Wait("r1", "triage", "gate", "verify.fix.person_triage", "findings.triage", "# Triage")],
            Findings: [Finding("F-1", FindingStates.Escalated, false), Finding("F-2", FindingStates.Declined, true), Finding("F-3", FindingStates.Declined, false)]));

        Assert.Equal("Triage 2 findings the triager could not clear", (await ViewAsync(world, "r1", "triage")).Title);
    }

    [Fact]
    public async Task An_open_gate_shows_each_consumed_artifacts_type_and_its_changes()
    {
        await using var world = new DecisionWorld();
        var run = await world.StartAsync();
        var requirements = await world.PutAsync("schema: 1\nchanges: [Split R2 in two, Dropped C4]\nrequirements: []\n");
        var untyped = await world.PutAsync("changes: [not read]\n");
        await world.AppendAsync(new RecordBatch(
            Waits: [new WaitRecord(run.RunId, "spec", "gate", "person_triage", "findings.triage", "# Approve", [], true, T0,
                [new ConsumedRef("requirements", "requirements", requirements), new ConsumedRef("notes", "notes", untyped)])],
            Artifacts: [new ArtifactRecord(run.RunId, "plan", 2, "requirements", "std:artifact_types/requirements", requirements, null, false, T0)]));

        var consumed = (await ViewAsync(world, run.RunId, "spec")).Consumed.ToDictionary(c => c.Name);

        Assert.Equal("std:artifact_types/requirements", consumed["requirements"].Type);
        Assert.Equal(["Split R2 in two", "Dropped C4"], consumed["requirements"].Changes!);
        Assert.Equal((null, null), (consumed["notes"].Type, consumed["notes"].Changes));
    }
}
