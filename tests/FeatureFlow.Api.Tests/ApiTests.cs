using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The API's authentication and authorisation, which need no Temporal: a request without the local token is
/// refused; an answer or override from an actor without the role it needs is refused before anything is
/// recorded or sent (docs/05 sections 3 to 5; F8: the actor and roles come from the credential, not the body).
/// </summary>
public sealed class ApiTests : IAsyncDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-api-" + Guid.NewGuid().ToString("N"));
    readonly List<Microsoft.AspNetCore.Builder.WebApplication> apps = [];
    readonly SqliteRecordStore records;

    public ApiTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    /// <summary>An API whose local token authenticates an actor with <paramref name="roles"/>; its Temporal client is absent.</summary>
    HttpClient Start(params string[] roles) => Start([], roles);

    HttpClient Start(IReadOnlyList<IAuthenticator> authenticators, params string[] roles)
    {
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
                ["artifact_store"] = new Dictionary<string, IProvider> { ["files"] = new FileSystemArtifactStore(Path.Combine(folder, "artifacts")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file", ["artifact_store"] = "files" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "someone", roles), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [],
            new PolicyConfig([new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, false))], null, null, null, null),
            Path.Combine(folder, "organisation.yaml"));
        var app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!) { Authenticators = authenticators }, "http://127.0.0.1:0");
        app.StartAsync().GetAwaiter().GetResult();
        apps.Add(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in apps)
            await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient http, string path, string body)
    {
        using var response = await http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    async Task OpenWaitAsync(string waitId, string kind, string type)
    {
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, now, null)],
            [new WaitRecord("r1", waitId, kind, "implement", type, "reason", [], true, now)]), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_request_without_the_local_token_is_refused()
    {
        var http = Start("viewer");
        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("runs", TestContext.Current.CancellationToken)).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("runs", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("runs", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("runs/nope", TestContext.Current.CancellationToken)).StatusCode);
    }

    /// <summary>
    /// A body is read after authentication (LOG 407): a caller with no credential is 401 whatever it sends, and an
    /// authenticated caller's malformed body is 400 with the one error shape, before anything is recorded.
    /// </summary>
    [Theory]
    [InlineData("runs", "not json")]
    [InlineData("runs", """{ "template": 3 }""")]
    [InlineData("runs", """{ "repositories": [] }""")]
    [InlineData("runs", """{ "template": "std:templates/ticket_delivery", "dry_run": "yes" }""")]
    [InlineData("runs", """{ "template": "std:templates/ticket_delivery", "parameters": { "n": 3 } }""")]
    [InlineData("runs", """{ "template": "std:templates/ticket_delivery", "colour": "blue" }""")]
    [InlineData("runs/r1/answers", """{ "outcome": "approved" }""")]
    [InlineData("runs/r1/answers", """{ "wait_id": null, "outcome": "approved" }""")]
    [InlineData("runs/r1/abort", "")]
    [InlineData("runs/r1/overrides", """{ "key": "skip", "reason": "r", "change": [] }""")]
    [InlineData("sessions/r1.gate-1/submit", """{ "revision": 7 }""")]
    [InlineData("sessions/r1.gate-1/checks", """{ "step": "check" }""")]
    public async Task A_malformed_body_is_400_with_an_error_and_without_a_credential_401(string path, string body)
    {
        var http = Start("admin");
        await OpenWaitAsync("gate-1", "gate", "ticket.spec_approval");
        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };

        var (unauthenticated, refusal) = await PostAsync(anonymous, path, body);
        var (status, text) = await PostAsync(http, path, body);

        Assert.Equal((HttpStatusCode.Unauthorized, "unauthenticated"), (unauthenticated, JsonSerializer.Deserialize<ErrorResponse>(refusal, ApiJson.Options)!.Error));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("the body ", JsonSerializer.Deserialize<ErrorResponse>(text, ApiJson.Options)!.Error, StringComparison.Ordinal);
        Assert.Empty((await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap());
    }

    /// <summary>An authenticator that accepts a request carrying <c>X-Test-Person</c> as that person, with fixed roles, through chat.</summary>
    sealed class HeaderAuthenticator(params string[] roles) : IAuthenticator
    {
        public Task<Caller?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, ApiServices services) =>
            Task.FromResult(context.Request.Headers["X-Test-Person"].ToString() is { Length: > 0 } person
                ? new Caller(person, roles, ["a-group"], CallerChannels.Chat)
                : null);
    }

    [Fact]
    public async Task The_first_authenticator_to_accept_a_request_decides_its_caller_and_none_accepting_is_refused()
    {
        var http = Start([new HeaderAuthenticator("viewer"), new LocalTokenAuthenticator()], "steerer");
        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("runs", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("runs", TestContext.Current.CancellationToken)).StatusCode);

        // The header authenticator comes first: its viewer, not the token's steerer, is refused the abort.
        await OpenWaitAsync("gate-1", "gate", "ticket.spec_approval");
        using var request = new HttpRequestMessage(HttpMethod.Post, "runs/r1/abort") { Content = new StringContent("""{ "reason": "stop" }""", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-Person", "pat");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("pat may not abort", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_answer_from_an_actor_without_the_rules_role_is_refused_and_nothing_is_recorded()
    {
        var http = Start("viewer");
        await OpenWaitAsync("gate-1", "gate", "ticket.spec_approval");

        // The token's actor is a viewer; the body cannot claim otherwise.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, "runs/r1/answers",
            """{ "wait_id": "gate-1", "outcome": "approved", "reason": "looks right" }""")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, "runs/r1/abort", """{ "reason": "stop" }""")).Status);
        var answers = (await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap();
        Assert.Empty(answers);
    }

    [Fact]
    public async Task Only_an_administrator_may_set_a_runs_hand_over_threshold_and_never_below_the_floor()
    {
        const string body = """{ "template": "std:templates/ticket_delivery", "continue_as_new_events": 50 }""";
        var steerer = Start("steerer");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(steerer, "runs", body)).Status);
        var admin = Start("admin");
        var (status, text) = await PostAsync(admin, "runs", body);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("at least 100", text);
    }

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("has space")]
    [InlineData("a..b")]
    [InlineData("ff/")]
    [InlineData("")]
    public async Task A_parent_branch_that_is_not_a_branch_name_is_refused(string branch)
    {
        var body = $$"""{ "template": "std:templates/ticket_delivery", "parent_branch": "{{branch}}" }""";

        var (status, text) = await PostAsync(Start("steerer"), "runs", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("parent_branch must be a branch name", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACME-1\nX")]
    public async Task A_tracker_issue_that_is_not_a_key_is_refused(string issue)
    {
        var body = $$"""{ "template": "std:templates/ticket_delivery", "tracker_issue": {{System.Text.Json.JsonSerializer.Serialize(issue)}} }""";

        var (status, text) = await PostAsync(Start("steerer"), "runs", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("tracker_issue must be a tracker key", text);
    }

    [Fact]
    public async Task An_internal_error_returns_a_generic_body()
    {
        // With no Temporal client, delivering the answer throws; the caller must not see the exception.
        var admin = Start("admin");
        await OpenWaitAsync("intervention-1", "intervention", "epic.escalation");
        var (status, text) = await PostAsync(admin, "runs/r1/answers", """{ "wait_id": "intervention-1", "action": "retry", "reason": "flaky" }""");
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal error", text);
    }

    static WaitRecord Wait(string kind, string type) => new("r1", "w", kind, "s", type, "", [], true, DateTimeOffset.UnixEpoch);

    static readonly PolicyConfig NoRules = new([], null, null, null, null);

    static readonly DecisionTypeItem[] Declared =
    [
        new("ticket.spec_approval", null, new SourceLocation("", 0, 0)) { DefaultRoles = ["approver"] },
        new("*.escalation", null, new SourceLocation("", 0, 0)) { DefaultRoles = ["steerer"] },
        new("x.audit", null, new SourceLocation("", 0, 0)) { DefaultRoles = ["auditor"] },
    ];

    [Fact]
    public void Who_may_answer_is_the_organisation_rule_then_the_library_default_then_the_fallback()
    {
        // A library default applies where no organisation rule matches (docs/05 section 4).
        Assert.Equal(["auditor"], ApiApp.RolesToAnswer(NoRules, Declared, Wait("gate", "x.audit")));
        // An organisation rule comes first.
        var rule = new PolicyConfig([new ApprovalRule("x.*", ["lead"], 1, false, new Cap<bool>(false, false))], null, null, null, null);
        Assert.Equal(["lead"], ApiApp.RolesToAnswer(rule, Declared, Wait("gate", "x.audit")));
        // Neither: approver for a decision, steerer for the rest.
        Assert.Equal(["approver"], ApiApp.RolesToAnswer(NoRules, [], Wait("gate", "y.other")));
        Assert.Equal(["steerer", "admin"], ApiApp.RolesToAnswer(NoRules, [], Wait("patch_approval", "ticket.patch_approval")));
    }

    [Fact]
    public void An_administrator_may_do_whatever_a_steerer_may()
    {
        Assert.Contains("admin", ApiApp.RolesToAnswer(NoRules, Declared, Wait("intervention", "ticket.escalation")));
        Assert.Contains("admin", ApiApp.RolesToAnswer(NoRules, [], Wait("intervention", "ticket.escalation")));
        Assert.DoesNotContain("admin", ApiApp.RolesToAnswer(NoRules, Declared, Wait("gate", "ticket.spec_approval")));
    }

    // ---- a person's triage on a decision's answer (LOG 202, 248) ----

    [Fact]
    public async Task An_answer_s_artifacts_are_stored_and_sent_by_reference_as_the_authenticated_person()
    {
        var approver = Start("approver");
        await OpenWaitAsync("gate-1", "gate", "ticket.spec_approval");
        const string Triage = "schema: 1\nentries:\n  - { finding: F-1, state: declined, reason: accepted }\n";

        // No Temporal client here: delivery fails after the answer is recorded as sent, which is what findings.triage reads.
        await PostAsync(approver, "runs/r1/answers", $$"""
            { "wait_id": "gate-1", "outcome": "triaged", "reason": "two are false positives", "artifacts": { "person_triage": {{System.Text.Json.JsonSerializer.Serialize(Triage)}} } }
            """);

        var sent = Assert.Single((await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" },
            TestContext.Current.CancellationToken)).Unwrap(), a => a.Result == "sent");
        Assert.Equal("someone", sent.Actor);
        var args = System.Text.Json.JsonSerializer.Deserialize<FeatureFlow.Interpreter.Wire.AnswerArgs>(sent.Answer, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("someone", args.Actor);
        var stored = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        await using var content = (await stored.GetAsync(new ArtifactRef(args.Artifacts!["person_triage"]), TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(Triage, await new StreamReader(content).ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("[]", "the body is not a valid request")]
    [InlineData("""{ "person_triage": 3 }""", "the body is not a valid request")]
    [InlineData("""{ "person_triage": { "schema": 1 } }""", "the body is not a valid request")]
    [InlineData("""{ "person_triage": "  " }""", "must be given as text")]
    public async Task Artifacts_that_are_not_documents_are_refused_before_anything_is_recorded(string artifacts, string error)
    {
        var approver = Start("approver");
        await OpenWaitAsync("gate-1", "gate", "ticket.spec_approval");

        var (status, text) = await PostAsync(approver, "runs/r1/answers", $$"""{ "wait_id": "gate-1", "outcome": "triaged", "reason": "r", "artifacts": {{artifacts}} }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(error, text);
        Assert.Empty((await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap());
    }

    [Fact]
    public async Task A_viewer_reads_the_artifacts_its_run_recorded_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var plan = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("items:\n  - name: one\n")), ct)).Unwrap().Id;
        var childs = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("# the child's design\n")), ct)).Unwrap().Id;
        var elsewhere = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("another run's\n")), ct)).Unwrap().Id;
        const string gone = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [
                new RunRecord("r1", null, "std:templates/epic_delivery", "running", "x", [], false, now, null),
                new RunRecord("r1.c", "r1", "std:templates/ticket_delivery", "running", "x", [], false, now, null),
                new RunRecord("r2", null, "std:templates/ticket_delivery", "running", "x", [], false, now, null),
            ],
            Artifacts:
            [
                new ArtifactRecord("r1", "tickets.plan", 1, "work_list", null, plan, null, false, now),
                new ArtifactRecord("r1.c", "plan", 1, "requirements", null, childs, null, false, now),
                new ArtifactRecord("r2", "plan", 1, "requirements", null, elsewhere, null, false, now),
                new ArtifactRecord("r1", "design", 1, "contract", null, gone, null, false, now),
            ]), ct);
        var viewer = Start("viewer");

        async Task<(HttpStatusCode, string)> ReadAsync(HttpClient http, string run, string reference)
        {
            using var response = await http.GetAsync($"runs/{run}/artifacts/{Uri.EscapeDataString(reference)}", ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }

        Assert.Equal((HttpStatusCode.OK, "items:\n  - name: one\n"), await ReadAsync(viewer, "r1", plan));
        Assert.Equal((HttpStatusCode.OK, "# the child's design\n"), await ReadAsync(viewer, "r1.c", childs));
        // A child's artifact is read through the child, whose scope may differ from its parent's (review R5, F1).
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync(viewer, "r1", childs)).Item1);
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync(viewer, "r1", elsewhere)).Item1);
        // Indexed, but no longer in the store (removed by hand or by a lifecycle rule): 404, not a server error (R5, F2).
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync(viewer, "r1", gone)).Item1);
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync(viewer, "r1.c", plan)).Item1);
        // The person who ratifies the plan reads it; a caller who may not see the run does not.
        Assert.Equal(HttpStatusCode.OK, (await ReadAsync(Start("approver"), "r1", plan)).Item1);
        Assert.Equal(HttpStatusCode.NotFound, (await ReadAsync(Start(), "r1", plan)).Item1);
    }

    [Fact]
    public async Task A_run_s_findings_ledger_reads_with_ledger_ids_and_states()
    {
        var reader = Start("viewer");
        var finding = new ReportedFinding("null-check", "Null", "x may be null", "high", true, FindingBases.Reasoned, new FindingLocation("acme/api", "src/A.cs", "A.B", "if (x)"));
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, DateTimeOffset.UnixEpoch, null)],
            Findings: [new FindingRecord("r1", "F-1", "fp", finding, "review", null, FindingStates.Escalated, null, null, 0, 0, 0, false, DateTimeOffset.UnixEpoch)]),
            TestContext.Current.CancellationToken);

        using var response = await reader.GetAsync("runs/r1/findings", TestContext.Current.CancellationToken);
        var view = JsonSerializer.Deserialize<FindingsResponse>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), ApiJson.Options)!;

        var only = Assert.Single(view.Findings);
        Assert.Equal(("F-1", FindingStates.Escalated, "null-check", "A.B"), (only.LedgerId, only.State, only.Rule, only.Location.Symbol));
        using var refused = await Start().GetAsync("runs/r1/findings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task A_runs_level_parent_step_tracker_issue_and_start_show_on_its_page_and_in_the_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        await records.AppendAsync(new RecordBatch([new RunRecord("r1", "p1", "std:templates/ticket_delivery", "running", "x", [], false, started.AddMinutes(5), null,
            "ticket", "deliver", "ACME-12", "Add DELETE /widgets/{id}", "https://acme.atlassian.net/browse/ACME-12", started)]), ct);
        var reader = Start("viewer");

        var page = JsonSerializer.Deserialize<RunDetail>(await reader.GetStringAsync("runs/r1", ct), ApiJson.Options)!.Run;
        var row = Assert.Single(JsonSerializer.Deserialize<RunList>(await reader.GetStringAsync("runs", ct), ApiJson.Options)!.Runs);

        Assert.Equal(("ticket", "deliver", "Add DELETE /widgets/{id}", started), (page.Level, page.ParentStepId, page.Title, page.StartedAt));
        Assert.Equal(new TrackerIssueView("ACME-12", "Add DELETE /widgets/{id}", "https://acme.atlassian.net/browse/ACME-12"), page.TrackerIssue);
        Assert.Equal(("ticket", "deliver", "Add DELETE /widgets/{id}", started), (row.Level, row.ParentStepId, row.Title, row.StartedAt));
        Assert.Equal(page.TrackerIssue, row.TrackerIssue);
    }

    [Theory]
    [InlineData("runs/r1/tree")]
    [InlineData("runs/r1/findings/F-1")]
    [InlineData("runs/r1/artifacts/sha256%3A00/document")]
    [InlineData("runs/r1/plan?proposed=patch_approval-1")]
    public async Task A_route_not_built_yet_answers_501_to_a_viewer_and_404_to_anyone_else(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await records.AppendAsync(new RecordBatch([new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, DateTimeOffset.UnixEpoch, null)]), ct);

        using var viewer = await Start("viewer").GetAsync(path, ct);
        using var other = await Start().GetAsync(path, ct);

        Assert.Equal((HttpStatusCode.NotImplemented, HttpStatusCode.NotFound), (viewer.StatusCode, other.StatusCode));
        Assert.Contains("not built yet (B1 ", await viewer.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_run_s_plan_is_shown_only_to_a_viewer_of_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await records.AppendAsync(new RecordBatch([new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, DateTimeOffset.UnixEpoch, null)]), ct);

        // The viewer gets past the run check to a run with no revision yet: nothing to show, not a refusal (LOG 493).
        using var viewer = await Start("viewer").GetAsync("runs/r1/plan", ct);
        Assert.Equal((HttpStatusCode.NoContent, ""), (viewer.StatusCode, await viewer.Content.ReadAsStringAsync(ct)));
        // A caller who may not see the run is told it does not exist.
        using var other = await Start().GetAsync("runs/r1/plan", ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Contains("no run r1", await other.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_run_lists_its_own_artifacts_by_kind_and_each_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        async Task<string> PutAsync(string text) => (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap().Id;
        var (requirements, evidence, transcript, prompt, revision, childs) =
            (await PutAsync("# requirements\n"), await PutAsync("{\"passed\": 3}"), await PutAsync("transcript text"), await PutAsync("the task"),
                await PutAsync("{\"revision\": 1}"), await PutAsync("child's"));
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [
                new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, now, null),
                new RunRecord("r1.c", "r1", "std:templates/ticket_delivery", "running", "x", [], false, now, null),
            ],
            Artifacts:
            [
                new ArtifactRecord("r1", "plan", 1, "requirements", "std:artifact_types/requirements", requirements, null, false, now),
                new ArtifactRecord("r1", "verify.tests", 1, "evidence:test_results", "test_results", evidence, "test.run", true, now),
                new ArtifactRecord("r1", "implement", 1, ArtifactNames.Transcript(1), ArtifactNames.TranscriptType, transcript, null, false, now),
                new ArtifactRecord("r1", "implement", 1, ArtifactNames.Prompt(1), ArtifactNames.PromptType, prompt, null, false, now),
                new ArtifactRecord("r1", "", 1, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, revision, null, false, now),
                new ArtifactRecord("r1.c", "plan", 1, "requirements", null, childs, null, false, now),
            ]), ct);
        var viewer = Start("viewer");

        using var listed = await viewer.GetAsync("runs/r1/artifacts", ct);
        var list = JsonSerializer.Deserialize<ArtifactList>(await listed.Content.ReadAsStringAsync(ct), ApiJson.Options)!;

        // The run's own index only: the child's artifact is listed through the child (LOG 325).
        Assert.Equal([ArtifactKind.Artifact, ArtifactKind.Evidence, ArtifactKind.Transcript, ArtifactKind.Prompt, ArtifactKind.PlanRevision],
            list.Artifacts.Select(a => a.Kind));
        foreach (var (reference, text) in new[] { (requirements, "# requirements\n"), (transcript, "transcript text"), (prompt, "the task"), (revision, "{\"revision\": 1}") })
            Assert.Equal(text, await viewer.GetStringAsync($"runs/r1/artifacts/{Uri.EscapeDataString(reference)}", ct));
        // Saved, never rendered or sniffed, should a browser navigate to it (review R1).
        using var read = await viewer.GetAsync($"runs/r1/artifacts/{Uri.EscapeDataString(requirements)}", ct);
        Assert.Equal(("nosniff", "attachment"), (read.Headers.GetValues("X-Content-Type-Options").Single(), read.Content.Headers.ContentDisposition?.DispositionType));
        using var child = await viewer.GetAsync("runs/r1.c/artifacts", ct);
        Assert.Equal(childs, Assert.Single(JsonSerializer.Deserialize<ArtifactList>(await child.Content.ReadAsStringAsync(ct), ApiJson.Options)!.Artifacts).Ref);
        using var refused = await Start().GetAsync("runs/r1/artifacts", ct);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task A_gate_names_what_its_step_consumed_and_a_child_reads_its_parents_artifact_its_gate_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        async Task<string> PutAsync(string text) => (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap().Id;
        var (contract, requirements, plan, unnamed) = (await PutAsync("the contract"), await PutAsync("# requirements"), await PutAsync("# plan"), await PutAsync("other"));
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [
                new RunRecord("r1", null, "std:templates/epic_delivery", "running", "x", [], false, now, null),
                new RunRecord("r1.c", "r1", "std:templates/ticket_delivery", "running", "x", [], false, now, null),
            ],
            [
                new WaitRecord("r1.c", "gate-1", "gate", "approve_spec", "ticket.spec_approval", "Approve?", ["approved", "rejected"], true, now,
                    [new ConsumedRef("requirements", "requirements", requirements), new ConsumedRef("delivery_plan", "delivery_plan", plan), new ConsumedRef("parent/contract", "contract", contract)]),
            ],
            Artifacts:
            [
                new ArtifactRecord("r1", "design", 1, "contract", null, contract, null, false, now),
                new ArtifactRecord("r1", "design", 1, "notes", null, unnamed, null, false, now),
                new ArtifactRecord("r1.c", "plan.plan", 1, "requirements", null, requirements, null, false, now),
                new ArtifactRecord("r1.c", "plan.plan", 1, "delivery_plan", null, plan, null, false, now),
            ]), ct);
        var viewer = Start("viewer");

        using var shown = await viewer.GetAsync("runs/r1.c", ct);
        var gate = Assert.Single(JsonSerializer.Deserialize<RunDetail>(await shown.Content.ReadAsStringAsync(ct), ApiJson.Options)!.Waits);
        Assert.Equal([("requirements", requirements), ("delivery_plan", plan), ("parent/contract", contract)], gate.Consumed.Select(c => (c.Key, c.Ref)));

        async Task<HttpStatusCode> ReadAsync(HttpClient http, string run, string reference)
        {
            using var response = await http.GetAsync($"runs/{run}/artifacts/{Uri.EscapeDataString(reference)}", ct);
            return response.StatusCode;
        }

        // The parent's contract the child's gate ratifies reads through the child (LOG 394); another of the parent's does not.
        Assert.Equal(HttpStatusCode.OK, await ReadAsync(viewer, "r1.c", contract));
        Assert.Equal(HttpStatusCode.NotFound, await ReadAsync(viewer, "r1.c", unnamed));
        Assert.Equal(HttpStatusCode.NotFound, await ReadAsync(Start(), "r1.c", contract));
    }
}
