using System.Net;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// M7's first done-when: every run-time override of docs/05 section 5 has a test showing who may and who may not use it,
/// with the default roles, with <c>policy.overrides</c> changing them, and for the protected and beyond-ceiling variants.
/// The verified-by: a request asserting another actor in its body is recorded and delivered with the authenticated one.
/// </summary>
public sealed class OverrideAuthorisationTests
{
    const string Run = "r1";

    static async Task<ApiWorld> WorldAsync(OrganisationConfig? organisation = null)
    {
        var world = new ApiWorld(organisation);
        await world.RunAsync(Run, "acme/api");
        await world.WaitAsync(Run, "decision-1", "decision", "ticket.spec_approval");
        await world.WaitAsync(Run, "question-1", "question", "ticket.question");
        await world.WaitAsync(Run, "intervention-1", "intervention", "ticket.escalation");
        await world.WaitAsync(Run, "patch-1", "patchapproval", "ticket.patch_approval");
        await world.WaitAsync(Run, "breakpoint-1", "breakpoint", "ticket.breakpoint");
        return world;
    }

    /// <summary>How each override key is asked for: its route and a body claiming the actor <c>mallory</c>.</summary>
    static (string Path, string Body) Request(string key) => key switch
    {
        Overrides.Answer => ($"runs/{Run}/answers", """{ "wait_id": "decision-1", "outcome": "approved", "actor": "mallory" }"""),
        Overrides.AnswerQuestion => ($"runs/{Run}/answers", """{ "wait_id": "question-1", "outcome": "yes", "actor": "mallory" }"""),
        Overrides.Recover => ($"runs/{Run}/answers", """{ "wait_id": "intervention-1", "action": "retry", "actor": "mallory" }"""),
        Overrides.Abort => ($"runs/{Run}/abort", """{ "reason": "stop", "actor": "mallory" }"""),
        _ => ($"runs/{Run}/overrides", $$"""{ "key": "{{key}}", "step_id": "implement", "reason": "test", "change": { "x": "1" }, "actor": "mallory" }"""),
    };

    /// <summary>
    /// Every key, a role that may use it by default and one that may not. Answers follow the approval rules: the engine's
    /// default for a decision or question is <c>approver</c>; a recovery is the <c>recover</c> override.
    /// </summary>
    [Theory]
    [InlineData(Overrides.Answer, "approver", "steerer")]
    [InlineData(Overrides.AnswerQuestion, "approver", "admin")]
    [InlineData(Overrides.Recover, "steerer", "approver")]
    [InlineData(Overrides.Recover, "admin", "viewer")]
    [InlineData(Overrides.Breakpoint, "steerer", "approver")]
    [InlineData(Overrides.Skip, "steerer", "viewer")]
    [InlineData(Overrides.Amend, "steerer", "approver")]
    [InlineData(Overrides.ChangeRole, "admin", "steerer")]
    [InlineData(Overrides.GrantAttempts, "steerer", "approver")]
    [InlineData(Overrides.OverrideLibrary, "steerer", "viewer")]
    [InlineData(Overrides.Abort, "steerer", "approver")]
    public async Task Each_override_by_default_is_for_its_roles_only_and_records_the_authenticated_actor(string key, string may, string mayNot)
    {
        await using var world = await WorldAsync();
        var (path, body) = Request(key);

        var (refused, refusal) = await ApiWorld.PostAsync(world.As("pat", mayNot), path, body);
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("pat may not", refusal, StringComparison.Ordinal);
        Assert.Empty(world.Temporal.Updates);
        Assert.Empty(await world.QueryAsync<AnswerRecord>(Queries.Answers, Run));
        Assert.Empty(await world.QueryAsync<OverrideRecord>(Queries.Overrides, Run));

        var (allowed, text) = await ApiWorld.PostAsync(world.As("lee", may), path, body);
        Assert.True(allowed == HttpStatusCode.OK, text);
        var delivered = Assert.Single(world.Temporal.Updates);
        Assert.Equal(Run, delivered.WorkflowId);
        // F8: the body's actor is ignored; the record and the delivered update carry the authenticated one.
        Assert.Equal("lee", delivered.Argument switch
        {
            AnswerArgs a => a.Actor,
            AbortArgs a => a.Actor,
            OverrideArgs a => a.Actor,
            _ => throw new InvalidOperationException($"unexpected {delivered.Argument}"),
        });
        var actors = (await world.QueryAsync<AnswerRecord>(Queries.Answers, Run)).Select(a => a.Actor)
            .Concat((await world.QueryAsync<OverrideRecord>(Queries.Overrides, Run)).Select(o => o.Actor)).ToList();
        Assert.NotEmpty(actors);
        Assert.All(actors, a => Assert.Equal("lee", a));
        Assert.DoesNotContain("mallory", JsonSerializer.Serialize(delivered.Argument));
    }

    [Theory]
    [InlineData(Overrides.Skip, Overrides.SkipProtected)]
    [InlineData(Overrides.Amend, Overrides.AmendProtected)]
    [InlineData(Overrides.OverrideLibrary, Overrides.OverrideLibraryProtected)]
    [InlineData(Overrides.GrantAttempts, Overrides.GrantAttemptsBeyond)]
    public async Task A_steerer_holds_no_variant_and_an_administrator_holds_it(string key, string variant)
    {
        await using var world = await WorldAsync();
        var (path, body) = Request(key);

        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("sam", "steerer"), path, body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("ada", "admin"), path, body)).Status);

        var args = world.Temporal.Updates.Select(u => (OverrideArgs)u.Argument!).ToList();
        Assert.Equal((RunWorkflowNames.Override, key), (world.Temporal.Updates[0].Update, args[0].Key));
        Assert.Empty(args[0].Variants!);
        Assert.Equal([variant], args[1].Variants);
    }

    [Fact]
    public async Task Granting_attempts_at_an_intervention_goes_beyond_the_hard_ceiling_only_for_an_administrator()
    {
        await using var world = await WorldAsync();
        const string body = """{ "wait_id": "intervention-1", "action": "grant_attempts", "attempts": 50 }""";

        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("al", "approver"), $"runs/{Run}/answers", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("sam", "steerer"), $"runs/{Run}/answers", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("ada", "admin"), $"runs/{Run}/answers", body)).Status);

        var args = world.Temporal.Updates.Select(u => (AnswerArgs)u.Argument!).ToList();
        Assert.Equal([false, true], args.Select(a => a.BeyondHardCeiling));
    }

    [Fact]
    public async Task Policy_overrides_change_who_may_use_a_key_and_its_variant()
    {
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null)
        {
            Overrides = new Dictionary<string, IReadOnlyList<string>>
            {
                [Overrides.Skip] = ["admin"],
                [Overrides.AmendProtected] = ["steerer"],
                [Overrides.ChangeRole] = ["steerer"],
                [Overrides.Abort] = ["admin"],
                [Overrides.Recover] = ["approver"],
            },
        }, "");
        await using var world = await WorldAsync(organisation);

        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("sam", "steerer"), Request(Overrides.Skip).Path, Request(Overrides.Skip).Body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("sam", "steerer"), Request(Overrides.Abort).Path, Request(Overrides.Abort).Body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("sam", "steerer"), Request(Overrides.Recover).Path, Request(Overrides.Recover).Body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("al", "approver"), Request(Overrides.Recover).Path, Request(Overrides.Recover).Body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("sam", "steerer"), Request(Overrides.ChangeRole).Path, Request(Overrides.ChangeRole).Body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("sam", "steerer"), Request(Overrides.Amend).Path, Request(Overrides.Amend).Body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("ada", "admin"), Request(Overrides.Skip).Path, Request(Overrides.Skip).Body)).Status);

        var amend = world.Temporal.Updates.Select(u => u.Argument).OfType<OverrideArgs>().Single(a => a.Key == Overrides.Amend);
        Assert.Equal([Overrides.AmendProtected], amend.Variants);
    }

    [Fact]
    public async Task A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away()
    {
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "")
        {
            Roles = new Dictionary<string, PeopleRoleConfig> { ["steerer"] = new(["leads"]), ["approver"] = new(["leads"]) },
            Teams = new Dictionary<string, TeamConfig>
            {
                ["payments"] = new("payments", ["acme/payments-*"], [], new Dictionary<string, PeopleRoleConfig> { ["steerer"] = new(["payments-leads"]) }),
            },
        };
        await using var world = new ApiWorld(organisation);
        await world.RunAsync("web", "acme/web");
        await world.RunAsync("pay", "acme/payments-api");
        const string body = """{ "key": "breakpoint", "step_id": "implement", "reason": "look", "change": { "set": "true" } }""";

        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("lee", groups: "leads"), "runs/web/overrides", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("lee", groups: "leads"), "runs/pay/overrides", body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("pia", groups: "payments-leads"), "runs/pay/overrides", body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("pia", groups: "payments-leads"), "runs/web/overrides", body)).Status);
        // Reading needs viewer, which a steerer holds; a person with no role in the scope does not see the run.
        using var response = await world.As("pia", groups: "payments-leads").GetAsync("runs", TestContext.Current.CancellationToken);
        var listed = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"pay\"", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"web\"", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams()
    {
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "")
        {
            Teams = new Dictionary<string, TeamConfig>
            {
                ["misc"] = new("misc", ["acme/misc"], [], new Dictionary<string, PeopleRoleConfig>()),
                ["pay"] = new("pay", ["acme/pay*"], [new ApprovalRule("*.merge_action", [], 3, false, new Cap<bool>(false, false))], new Dictionary<string, PeopleRoleConfig>()),
            },
        };
        await using var world = new ApiWorld(organisation);
        const string template = """{ "template": "std:templates/ticket_delivery", "repositories": [REPOS] }""";

        var (viewer, refusal) = await ApiWorld.PostAsync(world.As("vic", "viewer"), "runs", template.Replace("REPOS", "\"acme/web\"", StringComparison.Ordinal));
        Assert.Equal((HttpStatusCode.Forbidden, true), (viewer, refusal.Contains("vic may not start a run", StringComparison.Ordinal)));
        // Adding a repository of a looser team must not move the run out of pay's scope (LOG 217): refused before anything starts.
        var (spanning, why) = await ApiWorld.PostAsync(world.As("sam", "steerer"), "runs", template.Replace("REPOS", "\"acme/misc\", \"acme/pay\"", StringComparison.Ordinal));
        Assert.Equal((HttpStatusCode.BadRequest, true), (spanning, why.Contains("belong to more than one team (misc, pay)", StringComparison.Ordinal)));
        Assert.Empty(world.Temporal.Updates);
    }

    [Fact]
    public async Task Granting_attempts_at_an_intervention_needs_recover_and_grant_attempts()
    {
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null)
        {
            Overrides = new Dictionary<string, IReadOnlyList<string>> { [Overrides.Recover] = ["admin"], [Overrides.GrantAttempts] = ["steerer"] },
        }, "");
        await using var world = await WorldAsync(organisation);
        const string grant = """{ "wait_id": "intervention-1", "action": "grant_attempts", "attempts": 1 }""";

        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(world.As("sam", "steerer"), $"runs/{Run}/answers", grant)).Status);
        var noGrant = organisation with { Policy = organisation.Policy with { Overrides = new Dictionary<string, IReadOnlyList<string>> { [Overrides.GrantAttempts] = ["approver"] } } };
        await using var other = await WorldAsync(noGrant);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiWorld.PostAsync(other.As("sam", "steerer"), $"runs/{Run}/answers", grant)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(other.As("sam", "steerer"), $"runs/{Run}/answers",
            """{ "wait_id": "intervention-1", "action": "retry" }""")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(world.As("ada", "admin"), $"runs/{Run}/answers", grant)).Status);
    }

    [Theory]
    [InlineData("""{ "key": "answer", "reason": "r" }""", "is not made here")]
    [InlineData("""{ "key": "abort", "reason": "r" }""", "is not made here")]
    [InlineData("""{ "key": "skip_protected", "reason": "r" }""", "key must be one of")]
    [InlineData("""{ "key": "launch", "reason": "r" }""", "key must be one of")]
    [InlineData("""{ "key": "skip", "step_id": "implement" }""", "reason is required")]
    [InlineData("""{ "key": "skip", "reason": "r", "change": "x" }""", "change must be an object")]
    public async Task A_malformed_override_is_refused_before_anything_is_recorded(string body, string message)
    {
        await using var world = await WorldAsync();
        var (status, text) = await ApiWorld.PostAsync(world.As("ada", "admin"), $"runs/{Run}/overrides", body);
        Assert.Equal((HttpStatusCode.BadRequest, true), (status, text.Contains(message, StringComparison.Ordinal)));
        Assert.Empty(await world.QueryAsync<OverrideRecord>(Queries.Overrides, Run));
    }

    [Fact]
    public async Task An_override_is_recorded_before_and_after_delivery_with_its_reason_and_change()
    {
        await using var world = await WorldAsync();
        world.Temporal.Reply = "skipped";
        var (status, _) = await ApiWorld.PostAsync(world.As("sam", "steerer"), $"runs/{Run}/overrides",
            """{ "key": "skip", "step_id": "verify", "reason": "flaky", "change": { "outcome": "passed", "n": 2 } }""");

        Assert.Equal(HttpStatusCode.OK, status);
        var records = await world.QueryAsync<OverrideRecord>(Queries.Overrides, Run);
        Assert.Equal(["sent", "skipped"], records.Select(r => r.Result));
        Assert.All(records, r => Assert.Equal(("skip", "sam", "flaky", "verify"), (r.Key, r.Actor, r.Reason, r.StepId)));
        Assert.Equal(new Dictionary<string, string> { ["outcome"] = "passed", ["n"] = "2" }, records[0].Change);
    }
}
