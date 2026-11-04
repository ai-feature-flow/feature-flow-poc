using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M7's policy through a real run: the effective policy view in the run start record (docs/05 section 7), a team's
/// approval rule deciding auto-resolution in the run, and the verified-by: a request asserting another actor in its body
/// is recorded, and reaches the run, as the authenticated actor (F8).
/// </summary>
[Trait("Category", "Integration")]
public class PolicyRunTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task The_run_start_record_holds_the_effective_policy_and_a_teams_rule_decides_auto_resolution()
    {
        // The organisation allows auto-resolution everywhere; a team owning acme/api takes it away for ticket decisions
        // (a team tightens). Its lines go after the policy section, at the top level.
        await using var h = await Harness.StartAsync("policy-team", autoApprove: true, policy: """
            teams:
              api:
                repositories: [acme/api]
                policy: { approval: [{ match: "ticket.*", roles: [approver] }] }
            """);
        var outside = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-1", "--repo", "acme/web");
        var inside = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-1", "--repo", "acme/api");

        var (exit, end, error) = h.Cli("wait", outside, "--timeout", "120");
        Assert.True(exit == 0, end + error);
        Assert.Contains((await h.ShowAsync(outside))["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "auto_resolved" && e["stepId"]!.ToString() == "approve_spec");
        var (waitExit, wait, _) = h.Cli("wait", inside, "--for", "wait", "--timeout", "60");
        Assert.True(waitExit == 0 && wait.Contains("decision at approve_spec", StringComparison.Ordinal), wait);

        var policy = (await h.ShowAsync(inside))["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "policy").Select(e => e!["detail"]!.ToString()).ToList();
        Assert.Contains(policy, l => l.StartsWith("approve_spec auto-resolution: a person answers (auto not allowed)", StringComparison.Ordinal)
                                     && l.Contains("from: team api approval rule 'ticket.*'", StringComparison.Ordinal));
        Assert.Contains(policy, l => l.StartsWith("deliver.merge who merges:", StringComparison.Ordinal));
        Assert.Contains(policy, l => l.StartsWith("deliver.merge checks before merging: checks required", StringComparison.Ordinal));
        h.Cli("abort", inside, "--reason", "test done");
    }

    [Fact]
    public async Task An_actor_asserted_in_the_body_is_recorded_and_delivered_as_the_authenticated_actor()
    {
        await using var h = await Harness.StartAsync("policy-actor");
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-2", "--repo", "acme/api");
        var waitId = await h.WaitForWaitAsync(id, Timeout);
        Assert.NotNull(waitId);
        // The organisation allows no auto-resolution, so run start reported that the template's request has no effect.
        var start = (await h.ShowAsync(id))["timeline"]!.AsArray().Select(e => e!["detail"]?.ToString() ?? "").ToList();
        Assert.Contains(start, d => d.Contains("approve_spec auto-resolution: a person answers (auto not allowed)", StringComparison.Ordinal));

        using var http = new HttpClient { BaseAddress = new Uri(h.Api) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await File.ReadAllTextAsync(h.TokenFile, TestContext.Current.CancellationToken));
        async Task<HttpStatusCode> PostAsync(string path, string body)
        {
            using var response = await http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            return response.StatusCode;
        }

        // An override the run does not take yet (W1 builds its handler) is still recorded, as the caller, before delivery.
        await PostAsync($"runs/{id}/overrides", """{ "key": "breakpoint", "step_id": "implement", "reason": "look", "change": { "set": "true" }, "actor": "mallory" }""");
        Assert.Equal(HttpStatusCode.OK, await PostAsync($"runs/{id}/answers", $$"""{ "wait_id": "{{waitId}}", "outcome": "approved", "actor": "mallory" }"""));

        // The run records its timeline after the update returns.
        var deadline = DateTime.UtcNow + Timeout;
        string? answered = null;
        while (answered is null && DateTime.UtcNow < deadline)
        {
            answered = (await h.ShowAsync(id))["timeline"]!.AsArray().FirstOrDefault(e => e!["kind"]!.ToString() == "answered")?["detail"]?.ToString();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.EndsWith("by tester", answered);
        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var p = new Dictionary<string, string> { ["run_id"] = id };
        var answers = (await records.QueryAsync<AnswerRecord>(Queries.Answers, p, TestContext.Current.CancellationToken)).Unwrap();
        var overrides = (await records.QueryAsync<OverrideRecord>(Queries.Overrides, p, TestContext.Current.CancellationToken)).Unwrap();
        Assert.NotEmpty(answers);
        Assert.NotEmpty(overrides);
        Assert.All(answers, a => Assert.Equal("tester", a.Actor));
        Assert.All(overrides, o => Assert.Equal("tester", o.Actor));
        Assert.DoesNotContain(answers, a => a.Answer.Contains("mallory", StringComparison.Ordinal));
        h.Cli("abort", id, "--reason", "test done");
    }

    [Fact]
    public async Task The_cli_sends_an_override_with_its_reason_step_and_change_and_an_abort_asking_to_close_pull_requests()
    {
        await using var h = await Harness.StartAsync("policy-cli");
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-3", "--repo", "acme/api");
        Assert.NotNull(await h.WaitForWaitAsync(id, Timeout));

        var (badExit, _, bad) = h.Cli("override", id, "skip", "--reason", "r", "--change", "outcome");
        Assert.Equal((2, true), (badExit, bad.Contains("--change takes <name>=<value>", StringComparison.Ordinal)));
        h.Cli("override", id, "skip", "--step", "verify.tests", "--reason", "flaky tier", "--change", "outcome=passed", "--change", "note=a=b");
        var (abortExit, _, abortError) = h.Cli("abort", id, "--reason", "done", "--close-pull-requests");
        Assert.True(abortExit == 0, abortError);

        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var p = new Dictionary<string, string> { ["run_id"] = id };
        var sent = (await records.QueryAsync<OverrideRecord>(Queries.Overrides, p, TestContext.Current.CancellationToken)).Unwrap().First();
        Assert.Equal(("skip", "verify.tests", "flaky tier", "tester"), (sent.Key, sent.StepId, sent.Reason, sent.Actor));
        Assert.Equal(new Dictionary<string, string> { ["outcome"] = "passed", ["note"] = "a=b" }, sent.Change);
        var abort = (await records.QueryAsync<AnswerRecord>(Queries.Answers, p, TestContext.Current.CancellationToken)).Unwrap().First(a => a.WaitId == "abort");
        Assert.Equal("done (close pull requests)", abort.Answer);
    }
}
