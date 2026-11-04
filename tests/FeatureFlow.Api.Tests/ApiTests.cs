using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "runs/r1/abort") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-Person", "pat");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("pat may not abort", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_answer_from_an_actor_without_the_rules_role_is_refused_and_nothing_is_recorded()
    {
        var http = Start("viewer");
        await OpenWaitAsync("decision-1", "decision", "ticket.spec_approval");

        // The body claims an approver; the token's actor is a viewer.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, "runs/r1/answers",
            """{ "wait_id": "decision-1", "outcome": "approved", "actor": "lead", "roles": ["approver"] }""")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, "runs/r1/abort", "{}")).Status);
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
        var (status, text) = await PostAsync(admin, "runs/r1/answers", """{ "wait_id": "intervention-1", "action": "retry" }""");
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
        Assert.Equal(["auditor"], ApiApp.RolesToAnswer(NoRules, Declared, Wait("decision", "x.audit")));
        // An organisation rule comes first.
        var rule = new PolicyConfig([new ApprovalRule("x.*", ["lead"], 1, false, new Cap<bool>(false, false))], null, null, null, null);
        Assert.Equal(["lead"], ApiApp.RolesToAnswer(rule, Declared, Wait("decision", "x.audit")));
        // Neither: approver for a decision, steerer for the rest.
        Assert.Equal(["approver"], ApiApp.RolesToAnswer(NoRules, [], Wait("decision", "y.other")));
        Assert.Equal(["steerer", "admin"], ApiApp.RolesToAnswer(NoRules, [], Wait("patchapproval", "ticket.patch_approval")));
    }

    [Fact]
    public void An_administrator_may_do_whatever_a_steerer_may()
    {
        Assert.Contains("admin", ApiApp.RolesToAnswer(NoRules, Declared, Wait("intervention", "ticket.escalation")));
        Assert.Contains("admin", ApiApp.RolesToAnswer(NoRules, [], Wait("intervention", "ticket.escalation")));
        Assert.DoesNotContain("admin", ApiApp.RolesToAnswer(NoRules, Declared, Wait("decision", "ticket.spec_approval")));
    }

    // ---- a person's triage on a decision's answer (LOG 202, 248) ----

    [Fact]
    public async Task An_answer_s_artifacts_are_stored_and_sent_by_reference_as_the_authenticated_person()
    {
        var approver = Start("approver");
        await OpenWaitAsync("decision-1", "decision", "ticket.spec_approval");
        const string Triage = "schema: 1\nentries:\n  - { finding: F-1, state: declined, reason: accepted }\n";

        // No Temporal client here: delivery fails after the answer is recorded as sent, which is what findings.triage reads.
        await PostAsync(approver, "runs/r1/answers", $$"""
            { "wait_id": "decision-1", "outcome": "triaged", "actor": "someone-else", "artifacts": { "person_triage": {{System.Text.Json.JsonSerializer.Serialize(Triage)}} } }
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
    [InlineData("[]", "artifacts is a map")]
    [InlineData("""{ "person_triage": 3 }""", "must be given as text or a JSON map or list")]
    [InlineData("""{ "person_triage": "  " }""", "must be given as text or a JSON map or list")]
    public async Task Artifacts_that_are_not_documents_are_refused_before_anything_is_recorded(string artifacts, string error)
    {
        var approver = Start("approver");
        await OpenWaitAsync("decision-1", "decision", "ticket.spec_approval");

        var (status, text) = await PostAsync(approver, "runs/r1/answers", $$"""{ "wait_id": "decision-1", "outcome": "triaged", "artifacts": {{artifacts}} }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(error, text);
        Assert.Empty((await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap());
    }

    [Fact]
    public async Task A_run_s_findings_ledger_reads_with_ledger_ids_and_states()
    {
        var reader = Start("viewer");
        var finding = new ReportedFinding("null-check", "Null", "x may be null", "high", true, FindingBases.Reasoned, new FindingLocation("acme/api", "src/A.cs", "A.B", "if (x)"));
        await records.AppendAsync(new RecordBatch(Findings:
            [new FindingRecord("r1", "F-1", "fp", finding, "review", null, FindingStates.Escalated, null, null, 0, 0, 0, false, DateTimeOffset.UnixEpoch)]),
            TestContext.Current.CancellationToken);

        using var response = await reader.GetAsync("runs/r1/findings", TestContext.Current.CancellationToken);
        var view = ArtifactShapes.ReadLibrary<FindingsReport>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "the ledger");

        var only = Assert.Single(view.Findings);
        Assert.Equal(("F-1", FindingStates.Escalated), (only.LedgerId, only.State));
    }
}
