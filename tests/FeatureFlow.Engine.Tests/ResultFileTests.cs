using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary>TRX, JUnit and SARIF parsing, in one place for sandbox and CI tiers (LOG 107).</summary>
public sealed class ResultFileTests
{
    [Fact]
    public void TRX_counts_outcomes_and_keeps_each_failures_first_line()
    {
        var results = ResultFiles.Trx(ActionWorld.Trx(2, "A", "B"), "rev", "unit");

        Assert.Equal((2, 2, 1), (results.Passed, results.Failed, results.Skipped));
        Assert.Equal([new TestFailure("A", "A failed"), new TestFailure("B", "B failed")], results.Failures);
        Assert.False(results.Clean);
    }

    [Fact]
    public void JUnit_names_tests_by_class_and_name_and_counts_errors_as_failures()
    {
        const string text = """
            <testsuite name="s"><testcase classname="C" name="ok"/><testcase classname="C" name="bad"><error message="boom">x</error></testcase>
            <testcase classname="C" name="later"><skipped/></testcase></testsuite>
            """;

        var results = ResultFiles.JUnit(text, "rev", "it");

        Assert.Equal((1, 1, 1), (results.Passed, results.Failed, results.Skipped));
        Assert.Equal(new TestFailure("C.bad", "boom"), results.Failures.Single());
    }

    [Fact]
    public void Several_files_of_a_tier_are_summed()
    {
        var results = ResultFiles.Tests(ResultFormats.Junit, [ActionWorld.JUnit(2), ActionWorld.JUnit(1, "X")], "rev", "it");

        Assert.Equal((3, 1), (results.Passed, results.Failed));
    }

    [Theory]
    [InlineData("trx", "not xml at all")]
    [InlineData("trx", "<testsuites/>")]
    [InlineData("junit", "<TestRun/>")]
    [InlineData("junit", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><testsuites>&e;</testsuites>")]
    public void A_file_that_is_not_its_format_does_not_parse(string format, string text) =>
        Assert.Throws<FormatException>(() => ResultFiles.Tests(format, [text], "rev", "t"));

    [Fact]
    public void SARIF_takes_levels_from_the_result_then_the_rule_and_fingerprints_by_partial_fingerprint_else_by_content()
    {
        const string text = """
            { "runs": [ { "tool": { "driver": { "rules": [ { "id": "R1", "defaultConfiguration": { "level": "error" } } ] } },
              "results": [
                { "ruleId": "R1", "message": { "text": "first\nmore" }, "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 4 } } } ] },
                { "ruleId": "R2", "level": "none", "message": { "text": "n" }, "partialFingerprints": { "h": "abc" } },
                { "ruleId": "R3", "message": { "text": "w" } }
              ] } ] }
            """;

        var issues = ResultFiles.Sarif(text, "rev", "sarif").Issues;

        Assert.Equal([IssueLevels.Error, IssueLevels.Note, IssueLevels.Warning], issues.Select(i => i.Level));
        Assert.Equal(("a.cs", 4, "first"), (issues[0].Path, issues[0].Line, issues[0].Message));
        Assert.Equal("h:abc", issues[1].Fingerprint);
        Assert.StartsWith("sha256:", issues[0].Fingerprint, StringComparison.Ordinal);
        // The content fingerprint ignores the line, so an issue that moves is not new.
        var moved = ResultFiles.Sarif(text.Replace("\"startLine\": 4", "\"startLine\": 9", StringComparison.Ordinal), "rev2", "sarif").Issues;
        Assert.Equal(issues[0].Fingerprint, moved[0].Fingerprint);
    }

    const string Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Theory]
    [InlineData("PassedButRunAborted")]
    [InlineData("NotRunnable")]
    [InlineData("Inconclusive")]
    [InlineData("Timeout")]
    public void A_TRX_outcome_other_than_passed_or_not_executed_fails(string outcome)
    {
        var text = $"""<TestRun xmlns="{Ns}"><Results><UnitTestResult testName="T" outcome="{outcome}" /></Results></TestRun>""";

        var results = ResultFiles.Trx(text, "rev", "unit");

        Assert.Equal((0, 1), (results.Passed, results.Failed));
        Assert.Contains(outcome, results.Failures.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_TRX_run_that_aborted_fails_even_when_every_result_passed()
    {
        var text = $"""<TestRun xmlns="{Ns}"><ResultSummary outcome="Aborted" /><Results><UnitTestResult testName="T" outcome="Passed" /></Results></TestRun>""";
        var completed = text.Replace("Aborted", "Completed", StringComparison.Ordinal);

        Assert.Equal(1, ResultFiles.Trx(text, "rev", "unit").Failed);
        Assert.Equal(0, ResultFiles.Trx(completed, "rev", "unit").Failed);
    }

    [Fact]
    public void A_JUnit_suite_level_error_or_unattributed_failure_count_fails()
    {
        const string ownError = """<testsuite name="S"><error message="fixture setup failed"/><testcase classname="C" name="ok"/></testsuite>""";
        const string counts = """<testsuites><testsuite name="S" failures="2" errors="0"><testcase classname="C" name="ok"/><testcase classname="C" name="bad"><failure message="x"/></testcase></testsuite></testsuites>""";

        Assert.Equal("fixture setup failed", ResultFiles.JUnit(ownError, "rev", "it").Failures.Single().Message);
        Assert.Equal(2, ResultFiles.JUnit(counts, "rev", "it").Failed);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"version\": \"2.1.0\" }")]
    [InlineData("[]")]
    public void A_SARIF_file_without_runs_does_not_parse(string text) =>
        Assert.Throws<FormatException>(() => ResultFiles.Sarif(text, "rev", "sarif"));
}
