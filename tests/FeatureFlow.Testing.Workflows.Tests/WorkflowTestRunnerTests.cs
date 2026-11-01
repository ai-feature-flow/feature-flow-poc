using FeatureFlow.Model;

namespace FeatureFlow.Testing.Workflows.Tests;

/// <summary>Std's workflow tests (library/std/tests), and the runner's own behaviour on test files that must fail.</summary>
public class WorkflowTestRunnerTests
{
    static readonly string RepositoryRoot = FindRoot();
    static readonly string StdTests = Path.Combine(RepositoryRoot, "library", "std", "tests");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found above the test output folder");
    }

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
            return WorkflowTestRunner.RunPath(path, [Path.Combine(RepositoryRoot, "library", "std")]).Single();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_wrong_status_fails_the_test()
    {
        var result = RunVariant("ticket_main_path", "status: completed", "status: incomplete");

        Assert.False(result.Passed);
        Assert.Contains("status is completed, expected incomplete", result.Failures.Single());
    }

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
        var libraries = new Library.LibrarySet(Library.Library.Open(Path.Combine(RepositoryRoot, "library", "std"), new DiagnosticBag())!);

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
