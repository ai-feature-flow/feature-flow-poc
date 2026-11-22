using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// The planning tools on the agent worker (design note s1, s3): a proposal is checked by the run's preview query and staged in the
/// record store; the next accepted report carries it, re-checked; the endpoint caps proposals; a failed query is an engine fault.
/// </summary>
public sealed class PlanningEndpointTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), $"ff-planning-{Guid.NewGuid():N}");
    readonly SqliteRecordStore store;
    readonly Signals signals = new();
    readonly AgentEndpointServices services;
    static readonly StepClaims Claims = new("run-1/implement/1", "run-1", "wf-child", "implement", "branch", ["read", "shell"], 0, "claude");
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A run whose preview answers what the test sets, by workflow id; every query is kept.</summary>
    sealed class Signals : IRunSignals
    {
        public Func<string, object, PatchPreview> Preview { get; set; } = (_, _) => Answer(PatchPreview.Applies);

        public Func<string, AgentPlanQuery, AgentPlanView> Plan { get; set; } = (_, _) => throw new InvalidOperationException("no plan in this test");

        public List<(string WorkflowId, object Query)> Queries { get; } = [];

        public Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ModelUnreachableAsync(string workflowId, ModelUnreachableSignal signal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AgentPlanView> PlanAsync(string workflowId, AgentPlanQuery query, CancellationToken cancellationToken)
        {
            Queries.Add((workflowId, query));
            return Task.FromResult(Plan(workflowId, query));
        }

        public Task<PatchPreview> PreviewAsync(string workflowId, PatchPreviewQuery query, CancellationToken cancellationToken) => Previewed(workflowId, query);

        public Task<PatchPreview> ChildPreviewAsync(string workflowId, ChildPatchPreviewQuery query, CancellationToken cancellationToken) => Previewed(workflowId, query);

        Task<PatchPreview> Previewed(string workflowId, object query)
        {
            Queries.Add((workflowId, query));
            return Task.FromResult(Preview(workflowId, query));
        }
    }

    static PatchPreview Answer(string status, int revision = 4, string? forwardTo = null, bool review = false, params string[] said) =>
        new(status, status == PatchPreview.NeedsApproval ? said : [], status == PatchPreview.NeedsApproval ? [] : said, revision, forwardTo, review);

    public PlanningEndpointTests()
    {
        Directory.CreateDirectory(folder);
        store = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        services = new AgentEndpointServices(StepTokens.FromSecret("k"), store, signals, new Dictionary<string, ModelProxy>(), _ => null);
        (store.AppendAsync(new RecordBatch(AgentSteps:
        [
            new AgentStepRecord(Claims.StepKey, "run-1", "wf-child", "implement", "ticket", ReportAndHookTests.Contract, 2, 0, 3600, DateTimeOffset.UtcNow),
        ]), CancellationToken.None).GetAwaiter().GetResult()).Unwrap();
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    const string Proposal = """
        {"target":"self","base_revision":4,"resume_at":"migrate","reason":"the schema needs a migration",
         "changes":[{"op":"insert","after":"implement","step":{"id":"migrate","agent":{"role":"std:roles/implementer"}}}]}
        """;

    const string Report = """{"report":{"outcome":"implemented","summary":"done","outputs":{"answer":"x","count":1},"artifacts":{"notes":"notes.md"}}}""";

    async Task<IReadOnlyList<AgentEventRecord>> EventsAsync() =>
        (await store.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();

    async Task<IReadOnlyList<ReportRecord>> ReportsAsync() =>
        (await store.QueryAsync<ReportRecord>(Queries.Reports, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();

    [Fact]
    public async Task A_proposal_that_passes_is_staged_and_the_accepted_report_carries_it()
    {
        signals.Preview = (_, _) => Answer(PatchPreview.NeedsApproval, said: "implement has planning.review: required");
        var staged = await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);

        Assert.Equal((AgentEndpoint.PatchReply.Staged, PatchPreview.NeedsApproval, 4), (staged.Status, staged.Effect, staged.Revision));
        Assert.Contains("a person decides it, because: implement has planning.review: required", staged.Message, StringComparison.Ordinal);
        var query = Assert.IsType<PatchPreviewQuery>(Assert.Single(signals.Queries).Query);
        Assert.Equal((Claims.StepKey, false), (query.StepKey, query.ToParent));
        var patch = JsonNode.Parse(query.Patch)!;
        Assert.Equal(("agent", "implement", "the schema needs a migration", 4, "migrate", "migrate"),
            (patch["metadata"]!["proposed_by"]!["type"]!.ToString(), patch["metadata"]!["proposed_by"]!["step_id"]!.ToString(), patch["metadata"]!["reason"]!.ToString(),
                patch["base_revision"]!.GetValue<int>(), patch["resume_at"]!.ToString(), patch["changes"]![0]!["step"]!["id"]!.ToString()));
        Assert.Single(await EventsAsync(), e => e.Kind == AgentEndpoint.PatchStagedEvent);

        var reply = await AgentEndpoint.ReportAsync(services, Claims, Json(Report), Ct);

        Assert.True(reply.Accepted);
        var accepted = Assert.Single(await ReportsAsync()).Report!;
        Assert.Equal((query.Patch, false), (accepted.Patch, accepted.PatchToParent));
        // The report re-checked it against the plan as it is now.
        Assert.Equal(2, signals.Queries.Count);
    }

    [Fact]
    public async Task A_withdrawn_proposal_is_not_carried_and_a_refused_one_leaves_the_staged_one()
    {
        await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);
        signals.Preview = (_, _) => Answer(PatchPreview.Refused, said: "check 27: an agent may not change a step's role");
        var refused = await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);
        Assert.Equal(AgentEndpoint.PatchReply.Refused, refused.Status);
        Assert.Contains("- check 27: an agent may not change a step's role", refused.Message, StringComparison.Ordinal);
        Assert.Contains("earlier proposal (revision 4) is still staged", refused.Message, StringComparison.Ordinal);

        var withdrawn = await AgentEndpoint.ProposePatchAsync(services, Claims, Json("""{"withdraw":true}"""), Ct);
        Assert.Equal(AgentEndpoint.PatchReply.Withdrawn, withdrawn.Status);
        var queries = signals.Queries.Count;
        Assert.True((await AgentEndpoint.ReportAsync(services, Claims, Json(Report), Ct)).Accepted);

        Assert.Null(Assert.Single(await ReportsAsync()).Report!.Patch);
        Assert.Equal(queries, signals.Queries.Count);
    }

    [Fact]
    public async Task Past_five_proposals_an_attempt_is_told_its_limit_and_the_run_is_not_asked()
    {
        for (var i = 0; i < AgentEndpoint.MaxProposals; i++)
            Assert.Equal(AgentEndpoint.PatchReply.Staged, (await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct)).Status);
        var sixth = await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);

        Assert.Equal(AgentEndpoint.PatchReply.Limit, sixth.Status);
        Assert.Equal(AgentEndpoint.MaxProposals, signals.Queries.Count);
    }

    [Fact]
    public async Task A_proposal_too_large_or_that_does_not_read_is_not_sent_to_the_run()
    {
        var large = Json(Proposal.Replace("the schema needs a migration", new string('x', AgentEndpoint.MaxPatchBytes), StringComparison.Ordinal));
        Assert.Equal(AgentEndpoint.PatchReply.Limit, (await AgentEndpoint.ProposePatchAsync(services, Claims, large, Ct)).Status);
        var unknown = await AgentEndpoint.ProposePatchAsync(services, Claims,
            Json("""{"target":"self","base_revision":4,"resume_at":"x","reason":"r","changes":[{"op":"rename","target":"implement"}]}"""), Ct);
        Assert.Equal(AgentEndpoint.PatchReply.Refused, unknown.Status);
        Assert.Contains("check 1:", unknown.Message, StringComparison.Ordinal);
        var noResume = await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal.Replace("\"resume_at\":\"migrate\",", "", StringComparison.Ordinal)), Ct);
        Assert.Contains("resume_at is required", noResume.Message, StringComparison.Ordinal);
        Assert.Empty(signals.Queries);
        // Each counts towards the proposals an attempt allows, the oversized one too.
        Assert.Equal(3, (await EventsAsync()).Count(e => e.Kind == AgentEndpoint.PatchRefusedEvent));
    }

    [Fact]
    public async Task A_staged_proposal_the_plan_has_since_outgrown_rejects_the_report_for_a_correction_turn()
    {
        await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);
        signals.Preview = (_, _) => Answer(PatchPreview.Refused, revision: 5, said: "check 9: step migrate is unreachable");

        var reply = await AgentEndpoint.ReportAsync(services, Claims, Json(Report), Ct);

        Assert.True(reply is { Accepted: false, Stop: false });
        Assert.Contains("no longer passes its checks at revision 5: check 9: step migrate is unreachable", reply.Message, StringComparison.Ordinal);
        Assert.Contains("correction turn 1 of 2", reply.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(await ReportsAsync()).Accepted);
    }

    [Fact]
    public async Task A_proposal_for_the_parent_is_previewed_by_the_parent_with_the_step_s_review_and_carried_to_it()
    {
        signals.Preview = (workflow, query) => query switch
        {
            PatchPreviewQuery { ToParent: true } when workflow == "wf-child" => Answer(PatchPreview.Forward, forwardTo: "wf-parent", review: true),
            ChildPatchPreviewQuery when workflow == "wf-parent" => Answer(PatchPreview.NeedsApproval, revision: 9, said: "the child's step has planning.review: required"),
            _ => throw new InvalidOperationException($"unexpected {query} on {workflow}"),
        };
        var staged = await AgentEndpoint.ProposePatchAsync(services, Claims,
            Json("""{"target":"parent","base_revision":9,"reason":"after the tickets","changes":[{"op":"remove","target":"announce"}]}"""), Ct);

        Assert.Equal((AgentEndpoint.PatchReply.Staged, 9), (staged.Status, staged.Revision));
        var child = Assert.IsType<ChildPatchPreviewQuery>(signals.Queries[1].Query);
        Assert.Equal(("wf-child", "implement", true), (child.ChildWorkflowId, child.ProposerStepId, child.ReviewRequired));
        Assert.True((await AgentEndpoint.ReportAsync(services, Claims, Json(Report), Ct)).Accepted);
        Assert.True(Assert.Single(await ReportsAsync()).Report!.PatchToParent);
    }

    [Fact]
    public async Task Inspecting_the_parent_s_plan_asks_the_parent_as_a_child_and_shows_the_step_s_own_authority_and_staged_proposal()
    {
        AgentPlanView View(string status, string? forward, AgentPlanAuthority? authority, int revision) =>
            new(status, null, forward, "run-x", "ticket", revision, null, authority, "readable", [], []);
        signals.Plan = (workflow, query) => workflow == "wf-child"
            ? View(AgentPlanView.Forward, "wf-parent", new AgentPlanAuthority(["parent"], "required", false, null), 0)
            : View(AgentPlanView.Ok, null, new AgentPlanAuthority([], "", true, ["std:roles/implementer"]), 9);
        await AgentEndpoint.ProposePatchAsync(services, Claims, Json(Proposal), Ct);

        var answer = await AgentEndpoint.InspectPlanAsync(services, Claims, AgentEndpoint.Parent, Ct);

        var asked = Assert.IsType<AgentPlanQuery>(signals.Queries.Last(q => q.WorkflowId == "wf-parent").Query);
        Assert.Equal(("wf-child", "child"), (asked.StepKey, asked.Target));
        Assert.Equal(9, answer["revision"]!.GetValue<int>());
        Assert.Equal(["parent"], answer["authority"]!["targets"]!.AsArray().Select(t => t!.ToString()));
        Assert.Equal(("required", true), (answer["authority"]!["review"]!.ToString(), answer["authority"]!["require_review"]!.GetValue<bool>()));
        Assert.Equal("migrate", answer["staged"]!["patch"]!["resume_at"]!.ToString());
    }

    /// <summary>LOG 60: a failed query is the engine's fault, answered 503 for the client to retry; the agent is never told.</summary>
    [Fact]
    public async Task A_failed_query_is_answered_503()
    {
        signals.Preview = (_, _) => throw new InvalidOperationException("the workflow is not running");
        await using var app = AgentEndpoint.Build(services, "http://127.0.0.1:0");
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.Tokens.Issue(Claims with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }));

        var proposed = await client.PostAsync("/agent/v1/patches", new StringContent(Proposal, Encoding.UTF8, "application/json"), Ct);
        signals.Plan = (_, _) => throw new InvalidOperationException("the workflow is not running");
        var inspected = await client.GetAsync("/agent/v1/plan?target=self", Ct);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable), (proposed.StatusCode, inspected.StatusCode));
        Assert.DoesNotContain(await EventsAsync(), e => e.Kind.StartsWith("patch_", StringComparison.Ordinal));
    }
}
