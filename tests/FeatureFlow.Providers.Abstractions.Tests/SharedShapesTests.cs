namespace FeatureFlow.Providers.Abstractions.Tests;

public sealed class SharedShapesTests
{
    static ReportedFinding At(string snippet, string? symbol = "Orders.Add", string rule = "null-check") =>
        new(rule, "T", "D", "high", true, FindingBases.Reasoned, new FindingLocation("acme/api", "src/Orders.cs", symbol, snippet));

    [Fact]
    public void A_fingerprint_ignores_whitespace_and_blank_lines_in_the_snippet()
    {
        Assert.Equal(Fingerprints.Of(At("if (x)\n{\n  return;\n}")), Fingerprints.Of(At("  if   (x)\r\n\n{\n\treturn;\n}  ")));
    }

    [Fact]
    public void A_fingerprint_changes_with_the_code_the_symbol_or_the_rule()
    {
        var base_ = Fingerprints.Of(At("return x;"));

        Assert.NotEqual(base_, Fingerprints.Of(At("return y;")));
        Assert.NotEqual(base_, Fingerprints.Of(At("return x;", symbol: "Orders.Remove")));
        Assert.NotEqual(base_, Fingerprints.Of(At("return x;", rule: "other")));
    }

    [Fact]
    public void A_ledger_id_is_derived_from_the_run_and_the_fingerprint()
    {
        var fingerprint = Fingerprints.Of(At("return x;"));

        Assert.Equal(Fingerprints.LedgerId("run-1", fingerprint), Fingerprints.LedgerId("run-1", fingerprint));
        Assert.NotEqual(Fingerprints.LedgerId("run-1", fingerprint), Fingerprints.LedgerId("run-2", fingerprint));
        Assert.Matches("^F-[0-9A-F]{10}$", Fingerprints.LedgerId("run-1", fingerprint));
    }

    [Fact]
    public void A_reviewers_yaml_findings_report_reads_with_snake_case_keys()
    {
        const string yaml = """
            schema: 1
            angle: correctness
            findings:
              - rule: null-check
                title: Missing null check
                description: Add throws on null.
                required_correction: Throw ArgumentNullException
                severity: high
                blocking: true
                basis: demonstrated
                proof: OrdersTests.AddNull
                location: { repository: acme/api, file: src/Orders.cs, symbol: Orders.Add, snippet: "items.Add(item);" }
            """;

        var report = ArtifactShapes.ReadLibrary<FindingsReport>(yaml, "report");

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("correctness", "Throw ArgumentNullException", FindingBases.Demonstrated, "Orders.Add"),
            (report.Angle, finding.RequiredCorrection, finding.Basis, finding.Location.Symbol));
        Assert.Contains("\"required_correction\"", ArtifactShapes.WriteLibrary(report), StringComparison.Ordinal);
        Assert.DoesNotContain("ledger_id", ArtifactShapes.WriteLibrary(report), StringComparison.Ordinal);
        Assert.Equal(ArtifactShapes.WriteLibrary(report), ArtifactShapes.WriteLibrary(ArtifactShapes.ReadLibrary<FindingsReport>(ArtifactShapes.WriteLibrary(report), "again")));
    }

    [Fact]
    public void A_triage_reads_with_its_regression_test()
    {
        const string yaml = """
            schema: 1
            entries:
              - { finding: F-0123456789, state: declined, reason: By design, regression_test: { applicable: false, reason: No behaviour change } }
            """;

        var entry = Assert.Single(ArtifactShapes.ReadLibrary<Triage>(yaml, "triage").Entries);

        Assert.Equal(("F-0123456789", FindingStates.Declined, false), (entry.Finding, entry.State, entry.RegressionTest!.Applicable));
    }

    [Fact]
    public void Text_that_is_not_the_shape_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => ArtifactShapes.ReadLibrary<Triage>("schema: [1", "triage"));
        Assert.Throws<FormatException>(() => ArtifactShapes.ReadLibrary<Triage>("schema: one", "triage"));
    }

    [Fact]
    public void A_validator_refuses_an_undeclared_option_and_a_subject_of_another_type_but_not_an_absent_one()
    {
        var descriptor = new ValidatorDescriptor("v", "d", ["requirements"], new Dictionary<string, string> { ["strict"] = "boolean" }, []);
        ValidationRequest Request(IReadOnlyDictionary<string, string> options, params ValidationSubject[] subjects) => new("v", subjects, options, new Dictionary<string, string>(), [], []);
        var none = new Dictionary<string, string>();

        Assert.Null(descriptor.Refusal(Request(new Dictionary<string, string> { ["strict"] = "true" },
            new ValidationSubject("requirements", "std:artifact_types/requirements", new ArtifactRef("sha256:1")),
            new ValidationSubject("requirements", "std:artifact_types/requirements", null, "parent"))));
        Assert.NotNull(descriptor.Refusal(Request(new Dictionary<string, string> { ["other"] = "true" })));
        Assert.NotNull(descriptor.Refusal(Request(none, new ValidationSubject("plan", "std:artifact_types/delivery_plan", new ArtifactRef("sha256:1")))));
        Assert.NotNull(descriptor.Refusal(Request(none, new ValidationSubject("set", null, null, Members:
            [new ValidationSubject("plan", "std:artifact_types/delivery_plan", new ArtifactRef("sha256:1"))]))));
    }

    [Fact]
    public void A_result_is_valid_exactly_when_no_issue_is_an_error()
    {
        Assert.True(ValidationResult.Of([new ValidationIssue("w", ValidationSeverities.Warning, "m")]).Valid);
        Assert.False(ValidationResult.Of([new ValidationIssue("e", ValidationSeverities.Error, "m")]).Valid);
    }
}
