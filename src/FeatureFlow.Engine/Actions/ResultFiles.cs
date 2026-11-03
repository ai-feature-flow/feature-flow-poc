using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// Parses result files into evidence records, in one place for tiers run in a sandbox and in CI (LOG 107): TRX and
/// JUnit into <see cref="TestResults"/>, SARIF into <see cref="AnalysisResults"/>. A file that does not parse throws
/// <see cref="FormatException"/>, which the action turns into an intervention (docs/04 section 9.1, "cannot answer").
/// </summary>
public static class ResultFiles
{
    /// <summary>Test results of one tier from its result files, each in <paramref name="format"/> (<see cref="ResultFormats"/>).</summary>
    public static TestResults Tests(string format, IEnumerable<string> files, string revision, string tier)
    {
        var (passed, failed, skipped, failures) = (0, 0, 0, new List<TestFailure>());
        foreach (var text in files)
        {
            var one = format switch
            {
                ResultFormats.Trx => Trx(text, revision, tier),
                ResultFormats.Junit => JUnit(text, revision, tier),
                _ => throw new FormatException($"'{format}' is not a test result format"),
            };
            passed += one.Passed;
            failed += one.Failed;
            skipped += one.Skipped;
            failures.AddRange(one.Failures);
        }

        return new TestResults(revision, tier, passed, failed, skipped, failures);
    }

    /// <summary>
    /// A Visual Studio TRX file: each <c>UnitTestResult</c> by its <c>outcome</c>. <c>Passed</c> passes and <c>NotExecuted</c>
    /// is skipped; every other outcome (<c>Failed</c>, <c>Error</c>, <c>Timeout</c>, <c>Aborted</c>, <c>PassedButRunAborted</c>,
    /// <c>NotRunnable</c>, <c>Inconclusive</c> and the rest) fails. A run whose <c>ResultSummary</c> outcome is not a
    /// completed one fails too, even when no test result says so (a test host that crashed after its passes).
    /// </summary>
    public static TestResults Trx(string text, string revision, string tier)
    {
        var root = Xml(text, "TRX").Root!;
        if (root.Name.LocalName != "TestRun")
            throw new FormatException($"a TRX file's root is TestRun, not {root.Name.LocalName}");
        var (passed, failed, skipped, failures) = (0, 0, 0, new List<TestFailure>());
        foreach (var result in root.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName") ?? "(unnamed)";
            switch ((string?)result.Attribute("outcome"))
            {
                case "Passed":
                    passed++;
                    break;
                case "NotExecuted":
                    skipped++;
                    break;
                case var outcome:
                    failed++;
                    var message = result.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
                    failures.Add(new TestFailure(name, outcome == "Failed" && message is not null ? FirstLine(message) : $"outcome {outcome ?? "(none)"}: {FirstLine(message)}".TrimEnd(' ', ':')));
                    break;
            }
        }

        var run = (string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "ResultSummary")?.Attribute("outcome");
        if (run is not null and not ("Completed" or "Passed" or "Warning") && !(run == "Failed" && failed > 0))
        {
            failed++;
            failures.Add(new TestFailure("(test run)", $"the test run's outcome was {run}"));
        }

        return new TestResults(revision, tier, passed, failed, skipped, failures);
    }

    /// <summary>
    /// A JUnit XML file: each <c>testcase</c>, failed with a <c>failure</c> or <c>error</c>, skipped with <c>skipped</c>. A
    /// <c>testsuite</c>'s own <c>error</c> or <c>failure</c> (a fixture that failed), or <c>errors</c> and <c>failures</c>
    /// attributes counting more than its test cases show, fail too.
    /// </summary>
    public static TestResults JUnit(string text, string revision, string tier)
    {
        var root = Xml(text, "JUnit").Root!;
        if (root.Name.LocalName is not ("testsuites" or "testsuite"))
            throw new FormatException($"a JUnit file's root is testsuites or testsuite, not {root.Name.LocalName}");
        var (passed, failed, skipped, failures) = (0, 0, 0, new List<TestFailure>());
        foreach (var test in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testcase"))
        {
            var name = string.Join(".", new[] { (string?)test.Attribute("classname"), (string?)test.Attribute("name") }.Where(x => !string.IsNullOrEmpty(x)));
            var failure = test.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error");
            if (failure is not null)
            {
                failed++;
                failures.Add(new TestFailure(name, FirstLine((string?)failure.Attribute("message") ?? failure.Value)));
            }
            else if (test.Elements().Any(e => e.Name.LocalName == "skipped"))
                skipped++;
            else
                passed++;
        }

        foreach (var suite in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testsuite"))
        {
            var name = (string?)suite.Attribute("name") ?? "(suite)";
            foreach (var own in suite.Elements().Where(e => e.Name.LocalName is "failure" or "error"))
            {
                failed++;
                failures.Add(new TestFailure(name, FirstLine((string?)own.Attribute("message") ?? own.Value)));
            }

            var counted = suite.Elements().Count(c => c.Name.LocalName == "testcase" && c.Elements().Any(e => e.Name.LocalName is "failure" or "error"));
            var reported = Count(suite, "errors") + Count(suite, "failures");
            if (reported > counted)
            {
                failed += reported - counted;
                failures.Add(new TestFailure(name, $"the suite reports {reported} errors and failures, {counted} of them on test cases"));
            }
        }

        return new TestResults(revision, tier, passed, failed, skipped, failures);
    }

    /// <summary>
    /// A SARIF 2.1 log: every result of every run. A result's level is its own, else its rule's default, else
    /// <c>warning</c> (SARIF section 3.27.10); <c>none</c> counts as a note. The fingerprint is the first of
    /// <c>partialFingerprints</c>, else a hash of the rule, path and message, so it survives line moves.
    /// </summary>
    public static AnalysisResults Sarif(string text, string revision, string analyser)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new FormatException($"the SARIF file is not JSON: {e.Message}");
        }

        using (document)
        {
            try
            {
                return SarifIssues(document, revision, analyser);
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
            {
                // An entry of the wrong kind (a number where an object belongs): the file is not SARIF.
                throw new FormatException($"the SARIF file is not well formed: {e.Message}");
            }
        }
    }

    static AnalysisResults SarifIssues(JsonDocument document, string revision, string analyser)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
            throw new FormatException("a SARIF log has a runs list");
        var issues = new List<AnalysisIssue>();
        foreach (var run in runs.EnumerateArray())
        {
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            if (run.TryGetProperty("tool", out var tool) && tool.TryGetProperty("driver", out var driver) && driver.TryGetProperty("rules", out var rules)
                && rules.ValueKind == JsonValueKind.Array)
                foreach (var rule in rules.EnumerateArray())
                    if (Text(rule, "id") is { } id && rule.TryGetProperty("defaultConfiguration", out var config) && Text(config, "level") is { } level)
                        defaults[id] = level;
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var result in results.EnumerateArray())
            {
                var ruleId = Text(result, "ruleId") ?? "(no rule)";
                var level = (Text(result, "level") ?? defaults.GetValueOrDefault(ruleId) ?? IssueLevels.Warning) switch
                {
                    "error" => IssueLevels.Error,
                    "warning" => IssueLevels.Warning,
                    _ => IssueLevels.Note,
                };
                var message = result.TryGetProperty("message", out var m) ? Text(m, "text") ?? "" : "";
                string? path = null;
                int? line = null;
                if (result.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0
                    && locations[0].TryGetProperty("physicalLocation", out var physical))
                {
                    if (physical.TryGetProperty("artifactLocation", out var artifact))
                        path = Text(artifact, "uri");
                    if (physical.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var start) && start.TryGetInt32(out var n))
                        line = n;
                }

                var fingerprint = result.TryGetProperty("partialFingerprints", out var prints) && prints.ValueKind == JsonValueKind.Object
                                  && prints.EnumerateObject().FirstOrDefault() is { Value.ValueKind: JsonValueKind.String } first
                    ? $"{first.Name}:{first.Value.GetString()}"
                    : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleId}\n{path}\n{message}")));
                issues.Add(new AnalysisIssue(ruleId, level, path, line, FirstLine(message), fingerprint));
            }
        }

        return new AnalysisResults(revision, analyser, issues);
    }

    /// <summary>Whether <paramref name="level"/> is at least <paramref name="minimum"/>, in the order note, warning, error.</summary>
    public static bool AtLeast(string level, string minimum) => Rank(level) >= Rank(minimum);

    static int Rank(string level) => level switch { IssueLevels.Error => 2, IssueLevels.Warning => 1, _ => 0 };

    static XDocument Xml(string text, string what)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new FormatException($"the {what} file is not XML: {e.Message}");
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static int Count(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    static string FirstLine(string? text) => (text ?? "").Trim().Split('\n')[0].Trim();
}
