using System.Net;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// Approval rules at the moment of answering (docs/05 sections 3.2 and 4): the first matching rule wins over the library's
/// default; <c>min_count</c> distinct people must answer the same way before the answer is delivered; <c>from_chat</c>; and
/// the actor <c>policy</c> is never a person (F8, docs/04 section 4.2).
/// </summary>
public sealed class ApprovalTests
{
    const string Run = "r1";

    static readonly string Std = Path.Combine(Repository.Root, "library", "std");

    static OrganisationConfig Organisation(params ApprovalRule[] rules) =>
        new("acme", [new LibrarySourceConfig("std", "directory", Std)], new PolicyConfig(rules, null, null, null, null), "");

    static ApprovalRule Rule(string match, long minCount = 1, bool fromChat = false, params string[] roles) =>
        new(match, roles.Length == 0 ? ["approver"] : roles, minCount, fromChat, new Cap<bool>(false, false));

    static async Task<ApiWorld> WorldAsync(OrganisationConfig organisation, string type = "ticket.spec_approval", string kind = "gate")
    {
        var world = new ApiWorld(organisation);
        await world.RunAsync(Run, "acme/api");
        await world.WaitAsync(Run, "w1", kind, type);
        return world;
    }

    static Task<(HttpStatusCode Status, string Body)> AnswerAsync(HttpClient http, string outcome = "approved", string reason = "looks right") =>
        ApiWorld.PostAsync(http, $"runs/{Run}/answers", $$"""{ "wait_id": "w1", "outcome": "{{outcome}}", "reason": "{{reason}}" }""");

    [Theory]
    [InlineData("")]
    [InlineData(""", "reason": "  " """)]
    public async Task An_answer_or_an_abort_without_a_reason_is_refused_and_nothing_is_recorded(string reason)
    {
        await using var world = await WorldAsync(Organisation(Rule("*")));
        var admin = world.As("ann", "approver,steerer,admin");

        var (answer, answerText) = await ApiWorld.PostAsync(admin, $"runs/{Run}/answers", $$"""{ "wait_id": "w1", "outcome": "approved"{{reason}} }""");
        var (abort, abortText) = await ApiWorld.PostAsync(admin, $"runs/{Run}/abort", $$"""{ "close_pull_requests": false{{reason}} }""");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (answer, abort));
        Assert.All(new[] { answerText, abortText }, t => Assert.Contains("reason is required", t, StringComparison.Ordinal));
        Assert.Empty(world.Temporal.Updates);
        Assert.Empty(await world.QueryAsync<AnswerRecord>(Queries.Answers, Run));
    }

    [Fact]
    public async Task An_agents_question_is_answered_by_its_text_alone()
    {
        // LOG A3-0e: the question's answer is what the agent reads; no reason is asked for.
        await using var world = await WorldAsync(Organisation(Rule("*")), "ticket.question", kind: "question");
        var (status, text) = await ApiWorld.PostAsync(world.As("ann", "approver"), $"runs/{Run}/answers", """{ "wait_id": "w1", "outcome": "teal" }""");
        Assert.True(status == HttpStatusCode.OK, text);
        Assert.Null(((AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!).Reason);
    }

    [Fact]
    public async Task The_reason_is_delivered_with_the_answer_and_recorded_with_it()
    {
        await using var world = await WorldAsync(Organisation(Rule("*")));
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("ann", "approver"), "rejected", "requirements omit the audit log")).Status);

        var delivered = (AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!;
        Assert.Equal(("rejected", "requirements omit the audit log"), (delivered.Outcome, delivered.Reason));
        Assert.All(await world.QueryAsync<AnswerRecord>(Queries.Answers, Run), a => Assert.Equal("requirements omit the audit log", a.Reason));

        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("sam", "steerer,admin"), $"runs/{Run}/abort", """{ "reason": "superseded by FF-2" }""")).Status);
        Assert.Equal("superseded by FF-2", ((AbortArgs)world.Temporal.Updates[^1].Argument!).Reason);
        Assert.Contains(await world.QueryAsync<AnswerRecord>(Queries.Answers, Run), a => a.WaitId == "abort" && a.Reason == "superseded by FF-2");
    }

    [Fact]
    public async Task People_who_answer_the_same_way_in_different_words_count_together()
    {
        await using var world = await WorldAsync(Organisation(Rule("*.spec_approval", minCount: 2)));

        Assert.Equal(HttpStatusCode.Accepted, (await AnswerAsync(world.As("ann", "approver"), reason: "covers the audit log")).Status);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("cat", "approver"), reason: "matches the ticket")).Status);

        var delivered = (AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!;
        Assert.Equal(("ann, cat", "ann: covers the audit log; cat: matches the ticket"), (delivered.Actor, delivered.Reason));
        Assert.Equal("covers the audit log", (await world.QueryAsync<AnswerRecord>(Queries.Answers, Run))[0].Reason);
    }

    [Fact]
    public async Task Min_count_distinct_people_must_answer_the_same_way_before_the_answer_is_delivered()
    {
        await using var world = await WorldAsync(Organisation(Rule("*.spec_approval", minCount: 2)));

        var (first, firstText) = await AnswerAsync(world.As("ann", "approver"));
        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Contains("1 of 2 people", firstText, StringComparison.Ordinal);
        // The same person again does not count; someone answering another way does not count either.
        Assert.Equal(HttpStatusCode.Accepted, (await AnswerAsync(world.As("ann", "approver"))).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await AnswerAsync(world.As("bob", "approver"), "rejected")).Status);
        Assert.Empty(world.Temporal.Updates);

        var (second, _) = await AnswerAsync(world.As("cat", "approver"));
        Assert.Equal(HttpStatusCode.OK, second);
        var delivered = (AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!;
        Assert.Equal(("approved", "ann, cat"), (delivered.Outcome, delivered.Actor));
        // Each person's answer is recorded as they gave it.
        var answers = await world.QueryAsync<AnswerRecord>(Queries.Answers, Run);
        Assert.Equal(["ann", "ann", "bob", "cat", "cat"], answers.Select(a => a.Actor));
        Assert.Equal(["sent", "recorded"], answers.TakeLast(2).Select(a => a.Result));
    }

    [Fact]
    public async Task A_rule_with_min_count_one_delivers_the_first_answer()
    {
        await using var world = await WorldAsync(Organisation(Rule("*")));
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("ann", "approver"))).Status);
        Assert.Equal("ann", ((AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!).Actor);
    }

    [Fact]
    public async Task An_answer_through_chat_is_refused_where_the_rule_does_not_allow_chat()
    {
        await using var chatless = await WorldAsync(Organisation(Rule("*")));
        var (status, text) = await AnswerAsync(chatless.As("ann", "approver", channel: "chat"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("may not be answered from chat", text, StringComparison.Ordinal);
        Assert.Empty(chatless.Temporal.Updates);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(chatless.As("ann", "approver"))).Status);

        await using var chat = await WorldAsync(Organisation(Rule("*", fromChat: true)));
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(chat.As("ann", "approver", channel: "chat"))).Status);
    }

    [Fact]
    public async Task A_rule_on_merge_action_restricts_who_may_answer_the_merge_steps_decision()
    {
        // scm.merge's <level>.merge_action is a decision like any other: its rule, not the catch-all, says who answers (docs/04 section 4.5).
        await using var world = await WorldAsync(Organisation(Rule("*.merge_action", roles: "steerer"), Rule("*")), type: "ticket.merge_action");
        var (refused, text) = await AnswerAsync(world.As("ann", "approver"), "approve");
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("steerer", text, StringComparison.Ordinal);
        Assert.Empty(world.Temporal.Updates);

        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("sam", "steerer"), "approve")).Status);
        var delivered = (AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!;
        Assert.Equal(("approve", "sam"), (delivered.Outcome, delivered.Actor));
    }

    [Fact]
    public async Task The_first_matching_rule_wins_over_the_librarys_default_and_the_default_applies_where_none_matches()
    {
        // std declares *.escalation with default_approval steerer, and ticket.spec_approval with approver.
        await using var world = await WorldAsync(Organisation(Rule("ticket.*", roles: "steerer")));
        Assert.Equal(HttpStatusCode.Forbidden, (await AnswerAsync(world.As("ann", "approver"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("sam", "steerer"))).Status);

        await using var library = await WorldAsync(Organisation(Rule("epic.*", roles: "steerer")));
        Assert.Equal(HttpStatusCode.Forbidden, (await AnswerAsync(library.As("sam", "steerer"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(library.As("ann", "approver"))).Status);
    }

    [Fact]
    public async Task A_library_that_cannot_be_read_refuses_the_answer_rather_than_dropping_its_approval_rules()
    {
        // std reserves *.escalation for a steerer; with no organisation rule, its default decides.
        await using var readable = await WorldAsync(Organisation(), "epic.escalation");
        Assert.Equal(HttpStatusCode.Forbidden, (await AnswerAsync(readable.As("ann", "approver"))).Status);

        // The same library from a git source that does not clone: without it the engine's default (approver) would let ann in.
        var unreachable = new OrganisationConfig("acme", [new LibrarySourceConfig("std", "git", "", Path.Combine(Path.GetTempPath(), "ff-no-such-" + Guid.NewGuid().ToString("N") + ".git"), "main")],
            new PolicyConfig([], null, null, null, null), "");
        await using var world = await WorldAsync(unreachable, "epic.escalation");
        var (status, text) = await AnswerAsync(world.As("ann", "approver"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("library std cannot be read now; try again", text, StringComparison.Ordinal);
        Assert.Empty(world.Temporal.Updates);
        Assert.Empty(await world.QueryAsync<AnswerRecord>(Queries.Answers, Run));
    }

    [Fact]
    public async Task A_library_that_cannot_be_read_does_not_block_an_answer_it_cannot_change()
    {
        // An organisation rule for every type decides, so the unreadable library is not read, and an intervention's roles
        // come from the recover override: neither waits on a git host.
        var unreachable = new LibrarySourceConfig("acme", "git", "", Path.Combine(Path.GetTempPath(), "ff-no-such-" + Guid.NewGuid().ToString("N") + ".git"), "main");
        var governed = Organisation(Rule("*")) with { Libraries = [new LibrarySourceConfig("std", "directory", Std), unreachable] };
        await using var world = await WorldAsync(governed, "epic.escalation");
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(world.As("ann", "approver"))).Status);

        await using var recovery = await WorldAsync(Organisation() with { Libraries = [unreachable] }, "ticket.escalation", kind: "intervention");
        var (status, text) = await ApiWorld.PostAsync(recovery.As("sam", "steerer"), $"runs/{Run}/answers", """{ "wait_id": "w1", "action": "retry", "reason": "flaky" }""");
        Assert.True(status == HttpStatusCode.OK, text);
    }

    [Fact]
    public async Task A_teams_rule_comes_before_the_organisations_for_its_runs()
    {
        var organisation = Organisation(Rule("*")) with
        {
            Teams = new Dictionary<string, TeamConfig> { ["api"] = new("api", ["acme/api"], [Rule("ticket.*", minCount: 2)], new Dictionary<string, PeopleRoleConfig>()) },
        };
        await using var world = await WorldAsync(organisation);
        Assert.Equal(HttpStatusCode.Accepted, (await AnswerAsync(world.As("ann", "approver"))).Status);
    }

    [Fact]
    public async Task No_authenticated_caller_may_act_as_policy()
    {
        await using var world = await WorldAsync(Organisation(Rule("*")));
        var (status, text) = await AnswerAsync(world.As("policy", "approver,steerer,admin"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("actor of automatic answers", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("policy", "admin"), $"runs/{Run}/overrides",
            """{ "key": "skip", "reason": "r" }""")).Status);
        Assert.Empty(world.Temporal.Updates);
    }
}
