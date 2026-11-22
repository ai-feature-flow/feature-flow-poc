using FeatureFlow.Model;
using static FeatureFlow.Validation.Tests.Std;

namespace FeatureFlow.Validation.Tests;

/// <summary>
/// One negative test per load check: a realistic mutation of std's own templates and fragments that
/// triggers that check number and no other (docs/11, M1; LOG 70). Std itself validates clean
/// (<see cref="StdLibraryTests"/>), so each check is the thing that tells the two apart.
/// </summary>
public class MutationTests
{
    const string Ticket = "templates/ticket_delivery.yaml";
    const string FixLoop = "fragments/fix_loop.yaml";
    const string Merge = "fragments/merge_into_parent.yaml";
    const string DeliverInOrder = "fragments/deliver_in_order.yaml";
    const string VerifyAndReview = "fragments/verify_and_review.yaml";

    /// <param name="Message">Text every blocking diagnostic must contain, so that each mutation pins one rule
    /// of its check, and removing any rule fails a test.</param>
    public sealed record Mutation(int Check, string Message, string Description, Edit[] Edits);

    public static readonly IReadOnlyDictionary<string, Mutation> All = new Dictionary<string, Mutation>
    {
        ["unknown_key"] = new(1, "unknown key 'retries'", "an unknown key on the implement step", [
            new(Ticket,
                "    limits: { questions: { max: 3, timeout: 24h } }\n",
                "    limits: { questions: { max: 3, timeout: 24h } }\n    retries: 2\n")]),

        // LOG 501: the run's tracker issue comes from a parameter the template declares.
        ["tracker_parameter_undeclared"] = new(1, "metadata.tracker_parameter names 'ticket', which is not a declared string parameter",
            "the ticket names a tracker parameter it does not declare", [
            new(Ticket, "  tracker_parameter: ticket_id ", "  tracker_parameter: ticket ")]),

        ["unknown_action"] = new(2, "unknown action 'analysis.execute'", "verify_and_review's analysis step names an action that does not exist", [
            new(VerifyAndReview, "    action: analysis.run\n    with: { fail_on: new_issues }", "    action: analysis.execute\n    with: { fail_on: new_issues }")]),

        // LOG 342: notify.send was never built and is not an action.
        ["removed_action"] = new(2, "unknown action 'notify.send'", "a step names notify.send, removed from the actions", [
            new(VerifyAndReview, "    action: analysis.run\n    with: { fail_on: new_issues }", "    action: notify.send\n    with: { audience: team, message: analysed }")]),

        ["unknown_construct"] = new(2, "unknown construct with keys id, sleep", "an entry with no step form", [
            new(Ticket,
                "      reverify: verify\n",
                "      reverify: verify\n\n  - id: wait_for_review\n    sleep: 1h\n")]),

        ["duplicate_id"] = new(3, "step id 'implement' is used more than once", "the spec approval renamed to the implement step's id", [
            new(Ticket, "  - id: approve_spec\n", "  - id: implement\n")]),

        ["undeclared_parameter"] = new(4, "params.ticket is not a declared parameter", "implement binds a parameter the template does not declare", [
            new(Ticket, "    with: { ticket_id: { expr: \"params.ticket_id\" } }\n    consumes:", "    with: { ticket_id: { expr: \"params.ticket\" } }\n    consumes:")]),

        ["expression_parse"] = new(5, "does not parse", "a merge-bar guard that does not parse", [
            new(VerifyAndReview, "        - when: \"findings.open_blocking == 0\"\n", "        - when: \"findings.open_blocking ==\"\n")]),

        ["expression_unknown_output"] = new(5, "has no output 'to_fix'", "a guard reads an output findings.triage does not publish", [
            new(FixLoop, "steps.apply_triage.outputs.escalated > 0 ||", "steps.apply_triage.outputs.to_fix > 0 ||")]),

        ["guard_not_bool"] = new(5, "is int64, not bool", "a fix-loop guard reads a number as a condition", [
            new(FixLoop, "        - when: \"findings.open == 0\"\n          to: { exit: cleared }                   # the loop's first decision",
                "        - when: \"findings.open\"\n          to: { exit: cleared }                   # the loop's first decision")]),

        ["index_access"] = new(5, "index access on", "a fix-loop guard reaches an undeclared parameter and output by index", [
            new(FixLoop, "        - when: \"findings.open == 0\"\n          to: { exit: cleared }                   # the loop's first decision",
                "        - when: \"params['nope'] == 1 && steps['apply_triage'].outputs.bogus > 0\"\n          to: { exit: cleared }                   # the loop's first decision")]),

        ["declared_exhausted"] = new(6, "'attempts_exhausted' is added by the engine and may not be declared", "implement declares attempts_exhausted itself", [
            new(Ticket, "    outcomes: { implemented: success, blocked: blocked }", "    outcomes: { implemented: success, blocked: blocked, attempts_exhausted: failure }")]),

        ["no_outcomes"] = new(6, "a decision declares at least one outcome", "the partial-delivery decision declares no outcomes", [
            new(DeliverInOrder, "    outcomes: { retry: ~, fix_forward: ~, revert: ~ }\n", "")]),

        ["unrouted_outcome"] = new(7, "outcome 'retry' (class ~) has no route", "the partial-delivery decision's retry outcome, which has no class, loses its route", [
            new(DeliverInOrder, "      retry: deliver\n", "")]),

        ["unclassed_outcome"] = new(7, "outcome 'blocked' (class ~) has no route", "implement's blocked outcome loses its class, so no default routes it", [
            new(Ticket, "    outcomes: { implemented: success, blocked: blocked }", "    outcomes: { implemented: success, blocked: ~ }")]),

        ["fragment_without_defaults"] = new(7, "outcome 'blocked' (class blocked) has no route",
            "fix_loop loses its defaults: its blocked agents may not fall through to the template's (LOG 72)", [
            new(FixLoop, "\ndefaults:\n  routes:\n    by_class: { blocked: { exit: escalated } }   # a blocked triager or fixer leaves by the escalated exit\n", "")]),

        ["missing_target"] = new(8, "route target 'plan_spec' is not a step", "the spec approval's rejection routes to a step that does not exist", [
            new(Ticket, "      rejected: { to: plan, clean_up: true }", "      rejected: { to: plan_spec, clean_up: true }")]),

        ["clean_up_to_a_decision"] = new(8, "resets the work branch for decision approve_spec with clean_up", "a clean_up route aims at a decision, which records no start revision (re-check R3)", [
            new(Ticket, "      rejected: { to: plan, clean_up: true }", "      rejected: { to: approve_spec, clean_up: true }")]),

        ["guard_without_fallback"] = new(8, "does not end with a fallback", "open_pr's guarded route loses its fallback", [
            new(Merge, "          to: external_review\n        - to: checks\n", "          to: external_review\n")]),

        ["unreachable_step"] = new(9, "no route reaches this step", "a step after the last fragment, which every exit routes past", [
            new(Ticket,
                "      reverify: verify\n",
                "      reverify: verify\n\n  - id: announce\n    action: artifact.compose\n    with: { members: [requirements] }\n")]),

        ["cycle_without_exit"] = new(10, "has no route out", "the fix loop's validated tests go back to the fixer instead of leaving", [
            new(FixLoop, "      valid: { exit: fixed }\n      invalid: fix", "      valid: fix\n      invalid: fix")]),

        ["missing_producer"] = new(11, "consumes 'delivery_plans', which no step produces", "implement consumes an artifact no step produces", [
            new(Ticket, "      - { name: requirements }\n      - { name: delivery_plan }\n      - { name: contract, from: parent, optional: true }\n      - { name: baseline_test_results }",
                "      - { name: requirements }\n      - { name: delivery_plans }\n      - { name: contract, from: parent, optional: true }\n      - { name: baseline_test_results }")]),

        ["incompatible_producers"] = new(11, "neither type is compatible with the other", "the fixer produces implementation_claims as a different type from the implementer", [
            new(FixLoop, "        - { name: implementation_claims, type: std:artifact_types/evidence_claims }", "        - { name: implementation_claims, type: std:artifact_types/triage }")]),

        ["source_named_artifact"] = new(11, "may not produce an artifact named 'child'", "implement produces an artifact named like a source of consumed artifacts (LOG 153)", [
            new(Ticket, "      implemented: [{ name: implementation_claims, type: std:artifact_types/evidence_claims }]",
                "      implemented: [{ name: implementation_claims, type: std:artifact_types/evidence_claims }, { name: child, type: std:artifact_types/evidence_claims }]")]),

        ["agent_produces_evidence"] = new(12, "may not produce 'own_results' of evidence type", "implement produces an evidence type (F7)", [
            new(Ticket,
                "      implemented: [{ name: implementation_claims, type: std:artifact_types/evidence_claims }]",
                "      implemented: [{ name: implementation_claims, type: std:artifact_types/evidence_claims }, { name: own_results, type: std:artifact_types/test_results }]")]),

        // L9: an import registers files someone else wrote, so it may not stand in for the engine's evidence.
        ["import_produces_evidence"] = new(12, "artifact.import may not produce 'imported_results' of evidence type", "an import declares an evidence type for what it imports", [
            new(Ticket, "  - id: implement\n",
                "  - id: import\n    action: artifact.import\n    with: { commit: \"0000000000000000000000000000000000000000\", files: [{ path: r.trx, name: imported_results }] }\n" +
                "    produces:\n      imported: [{ name: imported_results, type: std:artifact_types/test_results }]\n\n  - id: implement\n")]),

        ["import_file_as_evidence"] = new(12, "artifact.import may not import 'notes' as evidence type", "an import checks a file as an evidence type", [
            new(Ticket, "  - id: implement\n",
                "  - id: import\n    action: artifact.import\n    with: { commit: \"0000000000000000000000000000000000000000\", files: [{ path: r.trx, name: notes, type: std:artifact_types/test_results }] }\n" +
                "    produces:\n      imported: [{ name: notes, type: std:artifact_types/requirements }]\n\n  - id: implement\n")]),

        ["reviewer_write_access"] = new(13, "exceeds role std:roles/reviewer's 'none'", "the verifying reviewer is given write access (F10)", [
            new(FixLoop, "      context: none\n", "      context: none\n      write_access: branch\n")]),

        ["step_sets_tools"] = new(13, "may not set tools", "implement sets its own tools (F10)", [
            new(Ticket, "      skills: [std:skills/solution_design, std:skills/verification]\n    with: { ticket_id",
                "      skills: [std:skills/solution_design, std:skills/verification]\n      tools: [shell]\n    with: { ticket_id")]),

        ["fragment_required_unbound"] = new(14, "required parameter 'subject_id' of fragment std:fragments/plan_and_validate is not bound",
            "the ticket's plan fragment loses its required subject_id binding", [
            new(Ticket, "      subject_id: { expr: \"params.ticket_id\" }\n", "")]),

        ["fragment_param_no_value"] = new(14, "{ param: planner_task } has no value",
            "planner_task becomes optional with no default, and the ticket stops binding it", [
            new("fragments/plan_and_validate.yaml", "  planner_task: { type: reference, required: true,", "  planner_task: { type: reference, required: false,"),
            new(Ticket, "      planner_task: std:tasks/plan_ticket\n", "")]),

        ["fragment_param_needs_literal"] = new(14, "{ param: planner_task } needs a literal value",
            "the ticket binds planner_task, which the fragment substitutes into a task field, to an expression", [
            new(Ticket, "      planner_task: std:tasks/plan_ticket\n", "      planner_task: { expr: \"params.ticket_id\" }\n")]),

        ["fragment_exit_unrouted"] = new(14, "exit 'reverify' (class ~) of fragment use 'deliver' is not routed", "merge_into_parent's reverify exit, which has no class, is left unrouted", [
            new(Ticket, "      reverify: verify\n", "")]),

        ["fragment_too_deep"] = new(14, "is nested 4 deep", "the review uses trace, which uses baseline: four fragments deep from a template", [
            new("fragments/standard_review.yaml", "steps:\n  - id: review\n", "steps:\n  - id: trace_review\n    use: std:fragments/trace\n\n  - id: review\n"),
            new("fragments/trace.yaml", "steps:\n  - id: trace\n", "steps:\n  - id: base\n    use: std:fragments/baseline\n\n  - id: trace\n")]),

        ["branch_routes"] = new(15, "a branch has no routes", "the revert branch step routes its own outcome", [
            new(DeliverInOrder, "        action: scm.revert\n        repository: { expr: \"item\" }\n",
                "        action: scm.revert\n        repository: { expr: \"item\" }\n        routes: { opened: report }\n")]),

        ["undeclared_decision_type"] = new(16, "'ticket.spec_approvel' is not declared", "the spec approval's decision type is misspelt", [
            new(Ticket, "      type: ticket.spec_approval\n", "      type: ticket.spec_approvel\n")]),

        ["plan_too_large"] = new(17, "the plan has 2027 steps", "a review over 1,000 angles", [
            new(Ticket, "      spec_refs: { expr: \"[steps.plan.plan.artifacts.requirements, steps.plan.plan.artifacts.delivery_plan]\" }\n",
                "      spec_refs: { expr: \"[steps.plan.plan.artifacts.requirements, steps.plan.plan.artifacts.delivery_plan]\" }\n      angles: ["
                + string.Join(", ", Enumerable.Range(0, 1000).Select(i => $"angle_{i}")) + "]\n")]),

        ["unresolved_reference"] = new(19, "std:roles/implementor does not resolve", "implement names a role that does not exist", [
            new(Ticket, "      role: std:roles/implementer\n", "      role: std:roles/implementor\n")]),

        // Check 31 on what the plan writes (LOG 346, 358, 359): every action's with is checked against its input schema at load.
        ["run_child_run_gate"] = new(31, "run.child has no input 'run_gate'", "a run.child step asks for run_gate, dropped by LOG 264", [
            new(Ticket, "  - id: implement\n",
                "  - id: spawn\n    action: run.child\n    with: { template: std:templates/ticket_delivery, run_gate: manual }\n\n  - id: implement\n")]),

        ["run_child_level"] = new(31, "run.child has no input 'level'", "plan_children's child step sets level, which is its template's", [
            new("fragments/plan_children.yaml", "          template: { param: child_template }\n", "          template: { param: child_template }\n          level: ticket\n")]),

        ["unknown_input"] = new(31, "scm.merge has no input 'force'", "the merge step asks for an input scm.merge does not take", [
            new(Merge, "      squash: { expr: \"params.squash\" }\n", "      squash: { expr: \"params.squash\" }\n      force: true\n")]),

        ["input_literal_value"] = new(31, "analysis.run input 'fail_on' must be one of new_issues, any_issues, never, not 'sometimes'", "the analysis step names a fail_on that does not exist", [
            new(VerifyAndReview, "    with: { fail_on: new_issues }", "    with: { fail_on: sometimes }")]),
    };

    public static TheoryData<string> Blocking() => new(All.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Blocking))]
    public void Mutation_triggers_its_check_and_no_other(string name)
    {
        var mutation = All[name];

        var result = Mutate(mutation.Edits);

        Assert.True(result.BlockingChecks.SequenceEqual([mutation.Check]),
            $"{mutation.Description}: expected check {mutation.Check} only, got [{string.Join(", ", result.BlockingChecks)}]\n{Describe(result)}");
        Assert.True(result.Diagnostics.Where(d => d.Blocks).All(d => d.Message.Contains(mutation.Message, StringComparison.Ordinal)),
            $"{mutation.Description}: every error should contain \"{mutation.Message}\"\n{Describe(result)}");
    }

    [Fact]
    public void Every_blocking_load_check_has_a_mutation()
    {
        int[] loadChecks = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17];

        Assert.Equal(loadChecks, All.Values.Select(m => m.Check).Where(c => c <= 17).Distinct().Order());
    }

    [Fact]
    public void Unused_parameter_warns_and_does_not_block()
    {
        var result = Mutate(new Edit(Ticket, "parameters:\n", "parameters:\n  notify_channel: { type: string, default: \"\" }\n"));

        Assert.False(result.HasErrors, Describe(result));
        Assert.Equal([34], result.WarningChecks);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("notify_channel"));
    }

    [Fact]
    public void Deprecated_reference_with_replacement_warns_and_does_not_block()
    {
        var result = Mutate(new Edit("roles/fixer.yaml", "  name: fixer\n", "  name: fixer\n  deprecated: { replacement: std:roles/implementer }\n"));

        Assert.False(result.HasErrors, Describe(result));
        Assert.Equal([35], result.WarningChecks);
    }

    /// <summary>
    /// Check 5 types a guard's reference to an action's output by the descriptor's outputs (docs/04 section 12; LOG 490):
    /// a name the action does not publish is refused, naming the ones it does; one it publishes loads.
    /// </summary>
    [Theory]
    [InlineData("repo.inspect", "{}", "inspected", "steps.act.outputs.{0}", "ci", "tier", "analyser, ci, integration_environment, manifest_hash, tags, tiers")]
    [InlineData("tracker.update", "{ op: comment, body: hello }", "done", "steps.act.outputs.{0} != ''", "issue", "key", "issue")]
    [InlineData("delivery.report", "{}", "complete", "size(steps.act.outputs.{0}) == 0", "not_merged", "reverted", "merged, not_merged")]
    public void A_guard_reading_an_output_its_action_does_not_publish_is_check_5(string action, string with, string outcome, string guard, string real, string unknown, string named)
    {
        string Template(string output) => $$"""
            api_version: feature-flow/v1
            kind: template
            metadata: { name: probe, level: ticket }
            defaults:
              routes:
                by_class: { failure: { escalate: true }, blocked: { escalate: true } }
            steps:
              - id: act
                action: {{action}}
                with: {{with}}
                routes:
                  {{outcome}}:
                    - when: "{{guard.Replace("{0}", output, StringComparison.Ordinal)}}"
                      to: { end: completed }
                    - to: { end: failed }
            """;

        var (_, _, accepted) = Probe(Template(real));
        Assert.False(accepted.HasErrors, string.Join("\n", accepted));

        var (_, _, refused) = Probe(Template(unknown));
        var error = Assert.Single(refused, x => x.Blocks);
        Assert.Equal(5, error.Check);
        Assert.Contains($"step act has no output '{unknown}' ({named})", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check 37 (LOG 515): a failure route into a step that consumes an artifact the routing step keeps only on success blocks;
    /// declaring it for the failure too clears it. Optional consumption counts.
    /// </summary>
    [Theory]
    [InlineData("[{ name: fix_test_results, type: std:artifact_types/test_results }]", true)]
    [InlineData("{ passed: [{ name: fix_test_results, type: std:artifact_types/test_results }], failed: [{ name: fix_test_results, type: std:artifact_types/test_results }] }", false)]
    public void A_failure_route_to_a_step_consuming_what_the_router_keeps_only_on_success_blocks(string produces, bool blocks)
    {
        var (_, _, d) = Probe($$"""
            api_version: feature-flow/v1
            kind: template
            metadata: { name: probe, level: ticket }
            artifact_types: [std:artifact_types/test_results]
            steps:
              - id: fix
                agent: { role: std:roles/implementer, task: std:tasks/repair_failures }
                consumes: [{ name: fix_test_results, optional: true }]
                outcomes: { fixed: success }
                routes: { fixed: retest }
              - id: retest
                action: test.run
                produces: {{produces}}
                routes: { passed: { end: completed }, failed: fix, baseline_failed: { end: failed } }
            """);

        var found = d.Where(x => x.Check == Checks.FailureRouteArtifact).ToList();
        Assert.Equal(blocks, d.HasErrors);
        if (!blocks)
        {
            Assert.Empty(found);
            return;
        }

        var error = Assert.Single(found);
        Assert.Equal(("retest", Severity.Error), (error.StepId, error.Severity));
        Assert.Contains("routes 'failed' to fix, which consumes 'fix_test_results', but this step keeps 'fix_test_results' only for passed", error.Message, StringComparison.Ordinal);
    }
}
