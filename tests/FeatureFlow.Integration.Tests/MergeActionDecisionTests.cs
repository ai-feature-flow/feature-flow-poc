using System.Text.Json.Nodes;
using FeatureFlow.Hosting;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// F2 (docs/12 A1; LOG 331, 341): std's <c>ticket_delivery</c> on the memory providers, with its default
/// <c>engine_can_merge: false</c> and an organisation rule requiring a person's approval on <c>*.merge_action</c>. The merge
/// step opens <c>ticket.merge_action</c>; approved, it asks a person to merge. A person who merges on the code host before
/// the answer is accepted: the decision closes as answered there, the timeline names who merged, and no intervention is raised.
/// </summary>
[Trait("Category", "Integration")]
public class MergeActionDecisionTests
{
    const string Policy = "    - { match: \"*.merge_action\", roles: [approver], auto: false }";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts a ticket and returns its run and the merge_action decision it opens, checking that decision's record.</summary>
    static async Task<(string Id, string Wait)> AtMergeDecisionAsync(Harness h, InMemoryForge forge, RepoRef repo, string ticket)
    {
        var id = h.Start("std:templates/ticket_delivery", "--param", $"ticket_id={ticket}", "--repo", "acme/api");
        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(3));
        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single(w => w!["waitId"]!.ToString() == waitId)!;
        Assert.Equal(("decision", "deliver.merge", "ticket.merge_action"), (wait["kind"]!.ToString(), wait["stepId"]!.ToString(), wait["decisionType"]!.ToString()));
        Assert.Equal(["approve", "reject"], wait["choices"]!.AsArray().Select(c => c!.ToString()));
        Assert.Equal(PullRequestStates.Open, (await new InMemoryCodeHost(forge).GetPullRequestAsync(new PullRequestRef(repo, 1), Ct)).Unwrap().State);
        return (id, waitId!);
    }

    /// <summary>A pull request event wakes the merge step's watch, so it reads again now rather than at its next poll.</summary>
    static Task WakeAsync(Harness h, string id) =>
        h.Client.GetWorkflowHandle(id).SignalAsync(RunWorkflowNames.Wake, [new WakeSignal("test", "pull_request.closed")]);

    [Fact]
    public async Task The_merge_action_decision_once_approved_asks_a_person_to_merge_and_their_merge_completes_the_run()
    {
        var (h, forge, repo) = await ForgeTicketTests.StartAsync("merge-action", policy: Policy);
        await using var _ = h;
        var (id, wait) = await AtMergeDecisionAsync(h, forge, repo, "T-1");

        // Before the answer no one has been asked to merge.
        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        Assert.DoesNotContain(await EventsAsync(records, id), e => e.Kind == "action_request");

        Assert.Equal(0, h.Cli("answer", id, wait, "approve").Exit);
        var asked = await UntilAsync(async () => (await EventsAsync(records, id)).FirstOrDefault(e => e.Kind == "action_request"));
        Assert.Equal("a person merges acme/api#1 into main, or closes it to decline", asked.Detail);
        Assert.Equal("running", (await h.ShowAsync(id))["run"]!["status"]!.ToString());

        forge.MergePullRequest(new PullRequestRef(repo, 1), "alice");
        await WakeAsync(h, id);
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());
        var timeline = Timeline(view);
        Assert.Contains(timeline, t => t.Kind == "answered" && t.StepId == wait && t.Detail == "approve by tester");
        Assert.Contains(timeline, t => t.Kind == "agent" && t.Detail.StartsWith("merged ", StringComparison.Ordinal) && t.Detail.Contains("by alice", StringComparison.Ordinal));
        Assert.DoesNotContain(timeline, t => t.Detail.Contains("not through Feature Flow", StringComparison.Ordinal) || t.Detail.Contains("not given through Feature Flow", StringComparison.Ordinal));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    [Fact]
    public async Task A_merge_on_the_code_host_before_the_answer_closes_the_decision_records_who_merged_and_the_run_continues()
    {
        var (h, forge, repo) = await ForgeTicketTests.StartAsync("merge-bypass", policy: Policy);
        await using var _ = h;
        var (id, wait) = await AtMergeDecisionAsync(h, forge, repo, "T-2");

        forge.MergePullRequest(new PullRequestRef(repo, 1), "bob");
        await WakeAsync(h, id);
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());
        Assert.Empty(view["waits"]!.AsArray());
        var timeline = Timeline(view);
        Assert.Contains(timeline, t => t.Detail == "bob merged acme/api#1 on the code host before ticket.merge_action was answered: the decision was not given through Feature Flow");
        Assert.Contains(timeline, t => t.Kind == "answered" && t.StepId == wait && t.Detail.StartsWith("on the code host, not through Feature Flow", StringComparison.Ordinal));
        Assert.DoesNotContain(timeline, t => t.Kind == "escalated");
        // The decision is closed: answering it now is refused.
        Assert.NotEqual(0, h.Cli("answer", id, wait, "approve").Exit);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    static async Task<IReadOnlyList<AgentEventRecord>> EventsAsync(SqliteRecordStore records, string id) =>
        (await records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = $"{id}/deliver.merge/1" }, Ct)).Unwrap();

    static async Task<T> UntilAsync<T>(Func<Task<T?>> read)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            if (await read() is { } value)
                return value;
            await Task.Delay(200, Ct);
        }

        throw new TimeoutException("the condition was not met within a minute");
    }

    static List<(string Kind, string StepId, string Detail)> Timeline(JsonNode view) =>
        view["timeline"]!.AsArray().Select(e => (e!["kind"]!.ToString(), e["stepId"]?.ToString() ?? "", e["detail"]?.ToString() ?? "")).ToList();
}
