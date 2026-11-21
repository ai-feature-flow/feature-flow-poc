using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// What needs the caller and who the caller is (docs/02 section 7, docs/05 section 3.2): <c>GET /attention</c> lists the open
/// waits the caller may answer now, by the one rule the answer path uses (<c>CheckAnswer</c>); <c>GET /me</c>; and the
/// history and <c>can_answer</c> on <c>GET /runs/{id}</c>.
/// </summary>
public sealed class AttentionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ApprovalRule Rule(string match, long minCount = 1, params string[] roles) =>
        new(match, roles.Length == 0 ? ["approver"] : roles, minCount, false, new Cap<bool>(false, false));

    /// <summary>
    /// An organisation whose rules decide every type the tests use, so no library is read; with people roles by group, and
    /// team <c>payments</c> owning <c>acme/payments</c>, whose approvers are <c>pay-leads</c>.
    /// </summary>
    static OrganisationConfig Organisation(params ApprovalRule[] rules) =>
        new OrganisationConfig("acme", [], new PolicyConfig(rules.Length == 0 ? [Rule("*")] : rules, null, null, null, null), "")
        {
            Roles = new Dictionary<string, PeopleRoleConfig> { ["viewer"] = new(["eng"]), ["approver"] = new(["leads"]) },
            Teams = new Dictionary<string, TeamConfig>
            {
                ["payments"] = new("payments", ["acme/payments"], [], new Dictionary<string, PeopleRoleConfig> { ["approver"] = new(["pay-leads"]) }),
                ["billing"] = new("billing", ["acme/billing"], [], new Dictionary<string, PeopleRoleConfig> { ["approver"] = new(["bill-leads"]) }),
            },
        };

    static async Task RunAsync(ApiWorld world, string runId, string status = "running", string repository = "acme/api") =>
        await world.Records.AppendAsync(new RecordBatch([new RunRecord(runId, null, "std:templates/ticket_delivery", status, "x", [repository], false, DateTimeOffset.UtcNow, null)]), Ct);

    static async Task<T> GetAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<T>(text, ApiJson.Options)!;
    }

    static async Task<IReadOnlyList<(string Run, string Wait)>> AttentionAsync(HttpClient http) =>
        (await GetAsync<AttentionList>(http, "attention")).Waits.Select(i => (i.Run.RunId, i.Wait.WaitId)).ToList();

    [Fact]
    public async Task Attention_lists_a_gate_for_an_approver_and_not_for_a_viewer()
    {
        await using var world = new ApiWorld(Organisation(Rule("ticket.*")));
        await RunAsync(world, "r1");
        await world.WaitAsync("r1", "gate-1", "gate", "ticket.spec_approval");

        var item = Assert.Single((await GetAsync<AttentionList>(world.As("ann", "approver"), "attention")).Waits);
        Assert.Equal(("r1", "gate-1", WaitKind.Gate, true, "ticket.spec_approval"), (item.Run.RunId, item.Wait.WaitId, item.Wait.Kind, item.Wait.CanAnswer, item.Wait.DecisionType));
        Assert.Empty(await AttentionAsync(world.As("vic", "viewer")));
    }

    [Fact]
    public async Task An_intervention_is_listed_for_a_steerer_only()
    {
        await using var world = new ApiWorld(Organisation());
        await RunAsync(world, "r1");
        await world.WaitAsync("r1", "intervention-1", "intervention", "epic.escalation");

        Assert.Equal([("r1", "intervention-1")], await AttentionAsync(world.As("sam", "steerer")));
        Assert.Empty(await AttentionAsync(world.As("ann", "approver")));
    }

    [Fact]
    public async Task A_team_scoped_approver_sees_only_its_teams_runs()
    {
        await using var world = new ApiWorld(Organisation());
        await RunAsync(world, "r-pay", repository: "acme/payments");
        await RunAsync(world, "r-api", repository: "acme/api");
        await world.WaitAsync("r-pay", "gate-1", "gate", "ticket.spec_approval");
        await world.WaitAsync("r-api", "gate-1", "gate", "ticket.spec_approval");

        Assert.Equal([("r-pay", "gate-1")], await AttentionAsync(world.As("pia", groups: "eng,pay-leads")));
        Assert.Equal(2, (await AttentionAsync(world.As("lee", groups: "leads"))).Count);
    }

    [Fact]
    public async Task An_ended_runs_open_wait_and_the_engines_status_waits_are_not_listed()
    {
        await using var world = new ApiWorld(Organisation());
        await RunAsync(world, "ended", "completed");
        await world.WaitAsync("ended", "gate-1", "gate", "ticket.spec_approval");
        await RunAsync(world, "r1");
        await world.WaitAsync("r1", "stalled-1", "stalled", "");
        await world.WaitAsync("r1", "gate-2", "gate", "ticket.spec_approval");

        Assert.Equal([("r1", "gate-2")], await AttentionAsync(world.As("ann", "approver")));
        var ended = await GetAsync<RunDetail>(world.As("ann", "approver"), "runs/ended");
        Assert.False(Assert.Single(ended.Waits).CanAnswer);
        Assert.Equal([("gate-2", true), ("stalled-1", false)], (await GetAsync<RunDetail>(world.As("ann", "approver"), "runs/r1")).Waits.Select(w => (w.WaitId, w.CanAnswer)));
    }

    /// <summary>The read and the answer path use one rule: for the same caller and wait, <c>can_answer</c> is true exactly when the answer is accepted.</summary>
    [Theory]
    [InlineData("ann", "approver", null, "gate", true)]
    [InlineData("vic", "viewer", null, "gate", false)]
    [InlineData("ann", "approver", "chat", "gate", false)]
    [InlineData("sam", "steerer", null, "intervention", true)]
    [InlineData("ann", "approver", null, "intervention", false)]
    public async Task Can_answer_agrees_with_the_answer_for_the_same_caller_and_wait(string person, string roles, string? channel, string kind, bool expected)
    {
        await using var world = new ApiWorld(Organisation(Rule("ticket.*")));
        await RunAsync(world, "r1");
        await world.WaitAsync("r1", "w1", kind, kind == "gate" ? "ticket.spec_approval" : "epic.escalation");
        var http = world.As(person, roles, channel: channel);

        var canAnswer = Assert.Single((await GetAsync<RunDetail>(http, "runs/r1")).Waits).CanAnswer;
        var body = kind == "gate" ? """{ "wait_id": "w1", "outcome": "approved", "reason": "fine" }""" : """{ "wait_id": "w1", "action": "retry", "reason": "fine" }""";
        var (status, text) = await ApiWorld.PostAsync(http, "runs/r1/answers", body);

        Assert.Equal(expected, canAnswer);
        Assert.True(status == (expected ? HttpStatusCode.OK : HttpStatusCode.Forbidden), $"{(int)status} {text}");
    }

    [Fact]
    public async Task Me_for_the_local_token_has_its_roles_everywhere_and_no_groups()
    {
        await using var world = new ApiWorld(Organisation(), [new LocalTokenAuthenticator()]);
        using var http = new HttpClient { BaseAddress = world.Address };
        http.DefaultRequestHeaders.Authorization = ApiWorld.Bearer("right");

        var me = await GetAsync<MeResponse>(http, "me");

        Assert.Equal(("local", "api", (IReadOnlyList<string>?)null), (me.Actor, me.Channel, me.Groups));
        Assert.Equal(["viewer", "approver", "steerer", "admin"], me.Roles);
        Assert.Equal(["billing", "payments"], me.Teams.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(me.Roles, me.Teams["payments"]);
    }

    [Fact]
    public async Task Me_for_identity_provider_groups_has_the_organisations_roles_and_each_teams_where_it_holds_any()
    {
        await using var world = new ApiWorld(Organisation());

        var me = await GetAsync<MeResponse>(world.As("pia", groups: "pay-leads", channel: "chat"), "me");

        Assert.Equal(("pia", "chat"), (me.Actor, me.Channel));
        Assert.Equal(["pay-leads"], me.Groups!);
        Assert.Empty(me.Roles);
        Assert.Equal(["payments"], me.Teams.Keys);
        Assert.Equal(["viewer", "approver"], me.Teams["payments"]);
    }

    [Theory]
    [InlineData("attention")]
    [InlineData("me")]
    public async Task A_new_route_without_a_credential_is_401(string path)
    {
        await using var world = new ApiWorld(Organisation());
        using var anonymous = new HttpClient { BaseAddress = world.Address };
        using var response = await anonymous.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", JsonSerializer.Deserialize<ErrorResponse>(await response.Content.ReadAsStringAsync(Ct), ApiJson.Options)!.Error);
    }

    [Fact]
    public async Task The_run_page_shows_closed_answers_min_count_progress_overrides_and_what_the_caller_may_override()
    {
        await using var world = new ApiWorld(Organisation(Rule("*.spec_approval", minCount: 2), Rule("*")));
        await RunAsync(world, "r1");
        var at = DateTimeOffset.UtcNow;
        // A plan gate answered and closed: its delivered answer is the one that closed it.
        await world.WaitAsync("r1", "gate-1", "gate", "ticket.plan_approval");
        var answer = JsonSerializer.Serialize(new AnswerArgs("gate-1", "ann", "approved", Reason: "the plan covers the ticket"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await world.Records.AppendAsync(new RecordBatch(
            Waits: [new WaitRecord("r1", "gate-1", "", "", "", "", [], false, at.AddMinutes(1))],
            Answers: [new AnswerRecord("r1", "gate-1", "ann", answer, "sent", at), new AnswerRecord("r1", "gate-1", "ann", answer, "recorded", at),
                new AnswerRecord("r1", "abort", "sam", "stop: wrong ticket", "refused: the run has ended", at)],
            Overrides: [new OverrideRecord("r1", "o1", "skip", "sam", "not needed", "docs", new Dictionary<string, string>(), "sent", at),
                new OverrideRecord("r1", "o1", "skip", "sam", "not needed", "docs", new Dictionary<string, string>(), "skipped docs", at),
                new OverrideRecord("r1", "o2", "amend", "sam", "tighten", "plan", new Dictionary<string, string> { ["with.limit"] = "3" }, "refused: no step plan", at)]), Ct);
        // A spec gate two people must approve, which one has.
        await world.WaitAsync("r1", "gate-2", "gate", "ticket.spec_approval");
        Assert.Equal(HttpStatusCode.Accepted, (await ApiWorld.PostAsync(world.As("ann", "approver"), "runs/r1/answers", """{ "wait_id": "gate-2", "outcome": "approved", "reason": "ok" }""")).Status);

        var page = await GetAsync<RunDetail>(world.As("sam", "steerer"), "runs/r1");

        Assert.Equal(["gate-2", "gate-1"], page.Waits.Select(w => w.WaitId));
        var progress = Assert.Single(page.Waits[0].Progress!);
        Assert.Equal(("approved", 2), (progress.Choice, progress.Needed));
        Assert.Equal(["ann"], progress.Actors);
        Assert.Null(page.Waits[0].Answer);
        Assert.Equal(("ann", "approved", "the plan covers the ticket"), (page.Waits[1].Answer!.Actor, page.Waits[1].Answer!.Choice, page.Waits[1].Answer!.Reason));
        Assert.False(page.Waits[1].CanAnswer);
        Assert.Equal([("gate-1", "approved", "sent"), ("gate-1", "approved", "recorded"), ("abort", "abort", "refused"), ("gate-2", "approved", "pending")],
            page.Answers.Select(a => (a.WaitId, a.Choice, a.Result.Split(':')[0])));
        Assert.Equal("stop: wrong ticket", page.Answers[2].Reason);
        Assert.Equal([("o1", "sent"), ("o1", "skipped docs"), ("o2", "refused: no step plan")], page.Overrides.Select(o => (o.OverrideId, o.Result)));
        Assert.Equal("3", page.Overrides[2].Change["with.limit"]);
        Assert.Contains(Overrides.Skip, page.OverridesAllowed);
        Assert.Contains(Overrides.Abort, page.OverridesAllowed);
        Assert.DoesNotContain(Overrides.SkipProtected, page.OverridesAllowed);
        Assert.DoesNotContain(Overrides.Recover, page.OverridesAllowed);
        Assert.Empty((await GetAsync<RunDetail>(world.As("ann", "approver"), "runs/r1")).OverridesAllowed);
        Assert.Contains(Overrides.SkipProtected, (await GetAsync<RunDetail>(world.As("ada", "admin"), "runs/r1")).OverridesAllowed);
    }
}
