using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M3's second "done when": a std <c>epic_delivery</c> run with child tickets runs across several
/// continues-as-new, driven through the CLI. Its planner's work list becomes child runs by plan.expand.
/// </summary>
[Trait("Category", "Integration")]
public class EpicRunTests
{
    /// <summary>
    /// The epic's design, its ticket plan and each ticket's plan and claims, as std's validators accept them: one epic
    /// requirement, supported by one criterion per ticket, which that ticket owns, copies into its own plan (its
    /// <c>subject_id</c>) and claims a passing test for (its <c>ticket_id</c>), so the epic's trace reaches the tickets' tests.
    /// </summary>
    const string Script = """
        {
          "epic_delivery:design": { artifacts: {
            requirements: { schema: 1, scope: { kind: epic, id: billing }, requirements: [
              { id: E-1, title: Invoices are complete, behaviour: Every invoice has its totals and export., supports_reason: The epic's goal } ] },
            contract: "---\nscope_kind: epic\nscope_id: billing\n---\n# Scope\n\nBilling.\n\n# Components and responsibilities\n\nThe API.\n\n# Contracts\n\nNone.\n\n# Data and control flow\n\nNone.\n\n# Failure handling\n\nNone.\n\n# Decisions\n\nNone.\n" } },
          "epic_delivery:tickets.plan": [{
            outcome: planned,
            outputs: { needs_integration: false },
            artifacts: {
              work_list: { schema: 1, level: ticket, items: [
                { name: invoice_totals, title: Totals, repositories: [acme/api], boundary: totals only, owns: [AC-invoice_totals] },
                { name: invoice_export, title: Export, repositories: [acme/api], boundary: export only, owns: [AC-invoice_export] },
                { name: invoice_audit, title: Audit, repositories: [acme/api], boundary: audit only, owns: [AC-invoice_audit] },
                { name: invoice_tax, title: Tax, repositories: [acme/api], boundary: tax only, owns: [AC-invoice_tax] },
                { name: invoice_email, title: Email, repositories: [acme/api], boundary: email only, owns: [AC-invoice_email] },
                { name: invoice_pdf, title: PDF, repositories: [acme/api], boundary: pdf only, owns: [AC-invoice_pdf] } ] },
              child_requirements: { schema: 1, scope: { kind: epic, id: billing }, acceptance_criteria: [
                { id: AC-invoice_totals, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_totals }, supports: [{ artifact: requirements, requirement: E-1 }] },
                { id: AC-invoice_export, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_export }, supports: [{ artifact: requirements, requirement: E-1 }] },
                { id: AC-invoice_audit, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_audit }, supports: [{ artifact: requirements, requirement: E-1 }] },
                { id: AC-invoice_tax, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_tax }, supports: [{ artifact: requirements, requirement: E-1 }] },
                { id: AC-invoice_email, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_email }, supports: [{ artifact: requirements, requirement: E-1 }] },
                { id: AC-invoice_pdf, title: A part of invoices works, behaviour: The part works., owner: { kind: ticket, id: invoice_pdf }, supports: [{ artifact: requirements, requirement: E-1 }] } ] } }
          }],
          "ticket_delivery:plan.plan": { artifacts: {
            requirements: { schema: 1, scope: { kind: ticket, id: "${subject_id}" }, requirements: [
              { id: R-1, title: The part works, behaviour: The part works., supports_reason: The ticket's own behaviour } ] },
            delivery_plan: { schema: 1, scope: { kind: ticket, id: "${subject_id}" }, acceptance_criteria: [
              { id: "AC-${subject_id}", title: A part of invoices works, behaviour: The part works.,
                supports: [{ artifact: parent/requirements, requirement: E-1 }, { artifact: requirements, requirement: R-1 }],
                verification: [{ id: unit, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required }] } ] } } },
          "ticket_delivery:implement": { delay: 4s, artifacts: { implementation_claims: { schema: 1, claims: [
            { criterion: "AC-${ticket_id}", verification: unit, tests: [{ repository: acme/api, tier: unit, id: Adds, change: new }] } ] } } }
        }
        """;

    [Fact]
    public async Task An_epic_with_children_runs_across_several_continues_as_new()
    {
        await using var h = await Harness.StartAsync("epic", Script, autoApprove: true, policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }");
        var id = h.Start("std:templates/epic_delivery", "--param", "epic_name=billing", "--param", "engine_can_merge=true",
            "--repo", "acme/api", "--continue-as-new-events", "100");

        // The ticket plan approval asks no auto-resolution, so a person approves it through the CLI.
        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(2));
        Assert.True(waitId is not null, (await h.ShowAsync(id)).ToJsonString());
        var (exit, _, error) = h.Cli("answer", id, waitId!, "approved");
        Assert.True(exit == 0, error + (await h.ShowAsync(id)).ToJsonString());

        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "240");
        var view = await h.ShowAsync(id);
        var childViews = string.Join("\n", await Task.WhenAll(view["children"]!.AsArray().Select(async c => (await h.ShowAsync(c!["runId"]!.ToString())).ToJsonString())));
        Assert.True((endExit, end.Trim()) == (0, $"run {id} completed"), view.ToJsonString() + "\n" + childViews);

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
