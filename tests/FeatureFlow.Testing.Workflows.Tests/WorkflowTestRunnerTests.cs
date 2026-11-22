using FeatureFlow.Model;

namespace FeatureFlow.Testing.Workflows.Tests;

/// <summary>Std's workflow tests (library/std/tests), and the runner's own behaviour on test files that must fail.</summary>
public class WorkflowTestRunnerTests
{
    static readonly string StdTests = Path.Combine(Repository.Root, "library", "std", "tests");

    public static TheoryData<string> StdTestFiles() =>
        new(Directory.GetFiles(StdTests, "*.yaml").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!);

    [Theory]
    [MemberData(nameof(StdTestFiles))]
    public void Std_workflow_test_passes(string name)
    {
        var result = WorkflowTestRunner.RunPath(Path.Combine(StdTests, name + ".yaml")).Single();

        Assert.True(result.Passed, string.Join("\n", result.Failures) + "\n" + string.Join("\n", result.Trace));
    }

    /// <summary>docs/11, M2 "done when": each template's main path, fix loop, exhausted attempts and race; partial delivery for projects.</summary>
    [Fact]
    public void Every_std_template_has_the_tests_the_build_plan_requires()
    {
        string[] required =
        [
            "ticket_main_path", "ticket_fix_loop", "ticket_fix_exhausted", "ticket_base_moved",
            "epic_main_path", "epic_fix_loop", "epic_fix_exhausted", "epic_base_moved",
            "project_main_path", "project_fix_loop", "project_plan_exhausted", "project_base_moved", "project_partial_delivery",
            "epic_child_failed", "ticket_spec_rejected", "ticket_stale_output", "ticket_work_failures",
        ];

        Assert.Empty(required.Except(Directory.GetFiles(StdTests, "*.yaml").Select(Path.GetFileNameWithoutExtension)));
    }

    /// <summary>Writes a variant of a std test to a temporary file, runs it against std, and deletes it.</summary>
    static WorkflowTestResult RunVariant(string from, string old, string replacement)
    {
        var text = File.ReadAllText(Path.Combine(StdTests, from + ".yaml"));
        Assert.Contains(old, text);
        var path = Path.Combine(Path.GetTempPath(), $"ff-variant-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, text.Replace(old, replacement, StringComparison.Ordinal));
        try
        {
            return WorkflowTestRunner.RunPath(path, [Path.Combine(Repository.Root, "library", "std")]).Single();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The runner answers as the shell does, through the core's one routing (<c>RunCore.Answer</c>, docs/04 section 4): a
    /// gate, a breakpoint and an intervention, each in a std test, are answered and recorded as a person's answer is.
    /// </summary>
    [Theory]
    [InlineData("ticket_main_path", "gate-", "approved by test")]
    [InlineData("ticket_spec_rejected", "gate-", "rejected by test: requirements omit the audit log")]
    [InlineData("ticket_breakpoint_before_merge", "breakpoint-", "continue by test")]
    [InlineData("epic_child_rerun", "intervention-", "go_to tickets.children_1 by test")]
    public void Each_wait_kind_is_answered_through_the_cores_routing(string name, string wait, string answer)
    {
        var result = WorkflowTestRunner.RunPath(Path.Combine(StdTests, name + ".yaml")).Single();

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        Assert.Contains(result.Trace, t => t.Kind == "answered" && t.StepId.StartsWith(wait, StringComparison.Ordinal) && t.Detail == answer);
    }

    [Fact]
    public void A_question_is_answered_through_the_cores_routing_times_out_or_closes_with_its_step()
    {
        var result = WorkflowTestRunner.RunPath(Path.Combine(StdTests, "ticket_agent_questions.yaml")).Single();

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        Assert.Contains(result.Trace, t => t is { Kind: "question_answered", StepId: "implement", Detail: "implement-q1 by test" });
        Assert.Contains(result.Trace, t => t is { Kind: "question_timed_out", StepId: "implement", Detail: "implement-q2" });
    }

    [Theory]
    [InlineData("implement-q2: timed_out", "implement-q2: answered", "question implement-q2 closed timed_out, expected answered")]
    [InlineData("answer: orders }", "answer: \"  \" }", "the answer to question implement-q1 was refused: implement-q1 is a question: answer with text")]
    [InlineData("{ ask: \"Is a migration wanted?\" }", "{ ask: \"Is a migration wanted?\", visit: 2 }", "question implement-q3 was not asked: its step had no visit 2")]
    public void A_question_that_does_not_close_as_expected_fails_the_test(string old, string replacement, string failure)
    {
        var result = RunVariant("ticket_agent_questions", old, replacement);

        Assert.False(result.Passed);
        Assert.Contains(failure, result.Failures);
    }

    /// <summary>
    /// A patch approval is answered through the core's routing too: an agent's patch that its step's <c>planning.review:
    /// required</c> holds for a person, approved or rejected by the file's <c>answers</c>.
    /// </summary>
    [Theory]
    [InlineData("approve", 2)]
    [InlineData("reject", 1)]
    public void A_patch_approval_is_answered_through_the_cores_routing(string answer, int revision)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ff-patch-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        File.WriteAllText(Path.Combine(root, "templates", "planned.yaml"), """
            api_version: feature-flow/v1
            kind: template
            metadata: { name: planned, level: ticket }
            defaults:
              routes:
                by_class: { failure: { escalate: true }, blocked: { escalate: true } }
            steps:
              - id: plan
                agent:
                  role: std:roles/planner
                  task: std:tasks/repair_failures
                  planning: { targets: [self], review: required }
                outcomes: { planned: success, blocked: blocked }
                routes: { planned: ship }
              - id: ship
                action: artifact.compose
                with: { name: release, members: [team] }
                routes: { composed: { end: completed } }
            """);
        var test = Path.Combine(root, "tests", "planned_patch.yaml");
        File.WriteAllText(test, $$"""
            api_version: feature-flow/v1
            kind: workflow_test
            name: planned_patch
            template: probe:templates/planned
            patch:
              api_version: feature-flow/v1
              kind: patch
              metadata: { proposed_by: { type: agent, step_id: plan }, reason: test }
              base_revision: 1
              resume_at: ship
              changes:
                - { op: set, target: ship, field: limits, value: { attempts: 2 } }
            answers:
              plan: {{answer}}
            expect:
              status: completed
              path: [plan, ship]
              validation_errors: [29]
            """);
        try
        {
            var result = WorkflowTestRunner.RunPath(test, [Path.Combine(Repository.Root, "library", "std")]).Single();

            Assert.True(result.Passed, string.Join("\n", result.Failures));
            Assert.Contains(result.Trace, t => t.Kind == "answered" && t.StepId.StartsWith("patch_approval-", StringComparison.Ordinal) && t.Detail == $"{answer} by test");
            Assert.Equal(revision, result.Trace.Count(t => t.Kind == "patch_applied") + 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An agent's patch that nothing holds for a person (<c>review: auto</c>, nothing protected) applies, and the step it inserts runs.</summary>
    [Fact]
    public void An_agents_patch_with_review_auto_applies_and_its_inserted_step_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ff-patch-applies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        File.WriteAllText(Path.Combine(root, "templates", "planned.yaml"), """
            api_version: feature-flow/v1
            kind: template
            metadata: { name: planned, level: ticket }
            defaults:
              routes:
                by_class: { failure: { escalate: true }, blocked: { escalate: true } }
            steps:
              - id: plan
                agent:
                  role: std:roles/planner
                  task: std:tasks/repair_failures
                  planning: { targets: [self], review: auto }
                outcomes: { planned: success, blocked: blocked }
                routes: { planned: ship }
              - id: ship
                action: artifact.compose
                with: { name: release, members: [team] }
                routes: { composed: { end: completed } }
            """);
        var test = Path.Combine(root, "tests", "planned_patch.yaml");
        File.WriteAllText(test, """
            api_version: feature-flow/v1
            kind: workflow_test
            name: planned_patch
            template: probe:templates/planned
            patch:
              api_version: feature-flow/v1
              kind: patch
              metadata: { proposed_by: { type: agent, step_id: plan }, reason: test }
              base_revision: 1
              resume_at: notice
              changes:
                - { op: insert, before: ship, step: { id: notice, action: artifact.compose, with: { name: notice, members: [team] }, routes: { composed: ship } } }
            expect:
              status: completed
              path: [plan, notice, ship]
            """);
        try
        {
            var result = WorkflowTestRunner.RunPath(test, [Path.Combine(Repository.Root, "library", "std")]).Single();

            Assert.True(result.Passed, string.Join("\n", result.Failures));
            Assert.Contains(result.Trace, t => t is { Kind: "patch_applied", Detail: "revision 2, by agent" });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_wrong_status_fails_the_test()
    {
        var result = RunVariant("ticket_main_path", "status: completed", "status: incomplete");

        Assert.False(result.Passed);
        Assert.Contains("status is completed, expected incomplete", result.Failures.Single());
    }

    /// <summary>
    /// <c>expect.consumed</c> (docs/09 section 4.3): the keys a step consumed, resolved, when an execution started. A key it
    /// did not consume, or an execution that never started, fails the test.
    /// </summary>
    [Theory]
    [InlineData("approve_spec: { 1: [requirements, delivery_plan] }", null)]
    [InlineData("approve_spec: { 1: [previous/requirements] }", "approve_spec execution 1 did not consume 'previous/requirements'; it consumed [requirements, delivery_plan]")]
    [InlineData("approve_spec: { 2: [requirements] }", "approve_spec execution 2 did not start, so what it consumed was not seen")]
    public void Consumed_keys_are_expected_per_execution(string expectation, string? failure)
    {
        // ticket_main_path already expects what its reviewers consume; the case is added to those.
        var result = RunVariant("ticket_main_path", "  consumed:", $"  consumed:\n    {expectation}");

        if (failure is null)
            Assert.True(result.Passed, string.Join("\n", result.Failures));
        else
            Assert.Equal(failure, Assert.Single(result.Failures));
    }

    /// <summary>
    /// <c>expect.branches</c> (docs/09 section 4.3): a <c>parallel</c>'s branches in the final plan, exactly and in order. A
    /// branch too many or missing, or an id that is not a container, fails the test.
    /// </summary>
    [Theory]
    [InlineData("tickets.children: [tickets.children_0, tickets.children_1]", null)]
    [InlineData("tickets.children: [tickets.children_0, tickets.children_1, tickets.children_2]", "tickets.children's branches are [tickets.children_0, tickets.children_1], expected [tickets.children_0, tickets.children_1, tickets.children_2]")]
    [InlineData("tickets.children: [tickets.children_0]", "tickets.children's branches are [tickets.children_0, tickets.children_1], expected [tickets.children_0]")]
    [InlineData("tickets.expand: [tickets.children_0]", "tickets.expand is not a parallel or chain in the final plan, so it has no branches")]
    public void Branches_are_expected_exactly_from_the_final_plan(string expectation, string? failure)
    {
        var result = RunVariant("epic_child_rerun", "expect:\n", $"expect:\n  branches: {{ {expectation} }}\n");

        if (failure is null)
            Assert.True(result.Passed, string.Join("\n", result.Failures));
        else
            Assert.Equal(failure, Assert.Single(result.Failures));
    }

    /// <summary>
    /// <c>expand:</c> as a list per execution (docs/09 section 4.3): the epic's work list gains a ticket when a person sends the
    /// run back to expand, so only the new ticket and the failed one run again (docs/03 section 10.4).
    /// </summary>
    [Fact]
    public void Expand_takes_a_list_per_execution_and_the_second_expansion_adds_only_the_new_item()
    {
        var result = RunVariant("epic_child_rerun", ExpandOnce, ExpandTwice);

        Assert.True(result.Passed, string.Join("\n", result.Failures) + "\n" + string.Join("\n", result.Trace));
        Assert.Contains(result.Trace, t => t is { Kind: "patch_applied", Detail: "revision 3, by engine" });
    }

    [Fact]
    public void A_branch_re_run_against_expectation_fails_an_expand_per_execution_test()
    {
        var result = RunVariant("epic_child_rerun", ExpandOnce, ExpandTwice.Replace("tickets.children_0: 1", "tickets.children_0: 2", StringComparison.Ordinal));

        Assert.False(result.Passed);
        Assert.Equal("tickets.children_0 ran 1 time(s), expected 2", Assert.Single(result.Failures));
    }

    const string ExpandOnce = """
        expand:
          tickets.expand:
            - { name: invoice_totals, repositories: [acme/api] }
            - { name: invoice_export, repositories: [acme/api] }
        script:
          tickets.children_1: [failed, completed]
        answers:
          tickets.children: { go_to: tickets.children_1 }

        expect:
          status: completed
          path_includes: [tickets.children_1, tickets.children_1, verify.tests, deliver.merge]
          executions:
            tickets.children_0: 1

        """;

    const string ExpandTwice = """
        expand:
          tickets.expand:
            - [{ name: invoice_totals, repositories: [acme/api] }, { name: invoice_export, repositories: [acme/api] }]
            - [{ name: invoice_totals, repositories: [acme/api] }, { name: invoice_export, repositories: [acme/api] }, { name: invoice_audit, repositories: [acme/api] }]
        script:
          tickets.children_1: [failed, completed]
        answers:
          tickets.children: { go_to: tickets.expand }

        expect:
          status: completed
          path_includes: [tickets.children_1, tickets.expand, tickets.children_1, tickets.children_2, verify.tests, deliver.merge]
          executions:
            tickets.children_0: 1
            tickets.children_2: 1
            tickets.expand: 2

        """;

    [Fact]
    public void A_path_out_of_order_fails_the_test()
    {
        var result = RunVariant("ticket_main_path", "    - plan.plan\n    - plan.validate\n", "    - plan.validate\n    - plan.plan\n");

        Assert.False(result.Passed);
        Assert.Contains("does not include, in order", result.Failures.Single());
    }

    [Fact]
    public void A_misspelt_step_fails_the_test()
    {
        var result = RunVariant("ticket_fix_loop", "verify.fix.verify_fix: [not_fixed, verified]", "verify.fix.verifyfix: [not_fixed, verified]");

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("'verify.fix.verifyfix' is not a step"));
    }

    [Fact]
    public void An_outcome_the_step_does_not_declare_fails_the_test()
    {
        var result = RunVariant("ticket_fix_loop", "[not_fixed, verified]", "[not_fixed, accepted]");

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("'accepted' is not an outcome"));
    }

    [Fact]
    public void An_unbounded_loop_is_stopped_and_fails_the_test()
    {
        // Granting a huge allowance stands in for a missing allowance check: the fix loop never stops.
        var test = WorkflowTestReader.Read(Path.Combine(StdTests, "ticket_fix_exhausted.yaml"), new DiagnosticBag())!;
        var libraries = new Library.LibrarySet(Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!);

        var result = WorkflowTestRunner.Run(test, libraries, new Interpreter.RunOptions { DefaultAttempts = 1_000_000, RunCeiling = 10_000_000 });

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("did not settle"));
    }

    [Fact]
    public void Infrastructure_failures_do_not_use_attempts()
    {
        var result = RunVariant("ticket_main_path", "expect:", """
            script:
              implement: { error: infrastructure, times: 5 }
            expect:
            """);

        Assert.True(result.Passed, string.Join("\n", result.Failures));
        Assert.Equal(5, result.Trace.Count(t => t is { Kind: "retried", StepId: "implement" }));
    }

    [Fact]
    public void A_patch_in_a_test_file_reports_the_checks_that_block_it()
    {
        var result = RunVariant("ticket_main_path", "expect:\n  status: completed", """
            patch:
              api_version: feature-flow/v1
              kind: patch
              metadata: { proposed_by: { type: human }, reason: stale }
              base_revision: 7
              resume_at: implement
              changes:
                - { op: remove, target: approve_spec }
            expect:
              validation_errors: [25]
              status: completed
            """);

        Assert.True(result.Passed, string.Join("\n", result.Failures));
    }
}
