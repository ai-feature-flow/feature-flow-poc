using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M3's second "done when": a std <c>epic_delivery</c> run with child tickets runs across several
/// continues-as-new, driven through the CLI. Its planner's work list becomes child runs by plan.expand.
/// </summary>
[Trait("Category", "Integration")]
public class EpicRunTests
{
    const string Script = """
        {
          "epic_delivery:tickets.plan": [{
            outcome: planned,
            outputs: { needs_integration: false },
            artifacts: { work_list: { schema: 1, level: ticket, items: [
              { name: invoice_totals, title: Totals, repositories: [acme/api], boundary: totals only, owns: [R1] },
              { name: invoice_export, title: Export, repositories: [acme/api], boundary: export only, owns: [R2] },
              { name: invoice_audit, title: Audit, repositories: [acme/api], boundary: audit only, owns: [R3] },
              { name: invoice_tax, title: Tax, repositories: [acme/api], boundary: tax only, owns: [R4] },
              { name: invoice_email, title: Email, repositories: [acme/api], boundary: email only, owns: [R5] },
              { name: invoice_pdf, title: PDF, repositories: [acme/api], boundary: pdf only, owns: [R6] } ] } }
          }],
          "ticket_delivery:implement": { delay: 4s }
        }
        """;

    [Fact]
    public async Task An_epic_with_children_runs_across_several_continues_as_new()
    {
        await using var h = await Harness.StartAsync("epic", Script, autoApprove: true);
        var id = h.Start("std:templates/epic_delivery", "--param", "epic_name=billing", "--param", "engine_can_merge=true",
            "--repo", "acme/api", "--continue-as-new-events", "100");

        // The ticket plan approval asks no auto-resolution, so a person approves it through the CLI.
        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(2));
        Assert.NotNull(waitId);
        var (exit, _, error) = h.Cli("answer", id, waitId!, "approved");
        Assert.True(exit == 0, error);

        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "240");
        var view = await h.ShowAsync(id);
        Assert.True((endExit, end.Trim()) == (0, $"run {id} completed"), view.ToJsonString());

        var children = view["children"]!.AsArray();
        Assert.Equal(6, children.Count);
        Assert.All(children, c => Assert.Equal("completed", c!["status"]!.ToString()));
        foreach (var name in Enumerable.Range(0, 6).Select(i => $"tickets.children_{i}"))
            Assert.Single(view["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == name);

        var runs = await h.RunsOfAsync(id);
        var final = await h.Client.GetWorkflowHandle(id).QueryAsync<RunView>(RunWorkflowNames.View, []);
        Assert.True(runs.Count >= 4, $"{runs.Count} run(s): expected at least 3 continues-as-new");
        Assert.Equal(runs.Count - 1, final.HandOvers.Count);
        Assert.Equal(final.HandOvers.Count, view["timeline"]!.AsArray().Count(e => e!["kind"]!.ToString() == "continued_as_new"));
        Assert.True(final.HandOvers.Count(o => o.RunningChildren > 0) >= 1,
            "no hand-over happened while children ran: " + string.Join("; ", final.HandOvers));
        Assert.All(final.HandOvers, o => Assert.Equal(0, o.InFlightActivities));
        Assert.Equal(0, final.ActivitiesStartedWhileDraining);
        var taskFailures = await h.WorkflowTaskFailuresAsync(id);
        Assert.True(taskFailures.Count == 0, "C5: workflow task failures: " + string.Join("; ", taskFailures));

        await h.RecordHistoryAsync(id, "epic_delivery");
        await h.RecordHistoryAsync(children[0]!["runId"]!.ToString(), "epic_delivery_child");
    }
}
