namespace FeatureFlow.Model.Tests;

/// <summary>The repository manifest, <c>feature-flow.yaml</c> (docs/07 section 4), F1's repository patterns and its organisation setting.</summary>
public class ManifestTests
{
    const string Full = """
        api_version: feature-flow/v1
        kind: repository
        environment: { from: .devcontainer/devcontainer.json, os: linux }
        setup: { commands: ["git config core.hooksPath .githooks", "dotnet tool restore"] }
        after_changes: { commands: ["dotnet format"] }
        test_tiers:
          - name: unit
            tags: [unit, fast]
            command: dotnet test tests/Unit
            result: { format: trx, path: tests/Unit/TestResults/unit.trx }
            targets: [sandbox, local, ci]
          - name: integration
            tags: [integration]
            command: dotnet test tests/Integration
            result: { format: trx, path: tests/Integration/TestResults/*.trx }
            targets: [ci]
            ci: { instance: github_actions, job: integration.yml }
            prerequisites: { env: [APP_BASE_URL] }
        static_analysis: { analyser: sarif, command: dotnet build, sarif_path: analysis.sarif }
        integration_environment: { image: "mcr.microsoft.com/dotnet/aspnet:9.0", start: dotnet run, port: 8080, ready_timeout: 10m }
        library:
          default_template: std:templates/ticket_delivery
        """;

    static (RepositoryManifest? Manifest, DiagnosticBag D) Read(string text)
    {
        var d = new DiagnosticBag();
        return (RepositoryManifest.Read(text, "sha256:x", "feature-flow.yaml", d), d);
    }

    [Fact]
    public void The_documented_example_reads_in_full()
    {
        var (m, d) = Read(Full);

        Assert.Empty(d);
        Assert.Equal(["git config core.hooksPath .githooks", "dotnet tool restore"], m!.Setup);
        Assert.Equal(["dotnet format"], m.AfterChanges);
        Assert.Equal(["unit", "integration"], m.Tiers.Select(t => t.Name));
        var it = m.Tiers[1];
        Assert.Equal(("integration.yml", "github_actions", "APP_BASE_URL"), (it.Ci!.Job, it.Ci.Instance, it.PrerequisiteEnvironment.Single()));
        Assert.Equal(("sarif", "analysis.sarif"), (m.Analysis!.Analyser, m.Analysis.SarifPath));
        Assert.Equal(8080L, m.Integration!.Port);
        Assert.NotNull(m.Library);
        Assert.Equal(["unit"], m.TiersTagged(["fast"]).Select(t => t.Name));
        Assert.Equal(2, m.TiersTagged([]).Count);
        Assert.Equal(["e2e"], m.UnknownTags(["unit", "e2e", "e2e"]));
    }

    [Theory]
    [InlineData("statc_analysis: { analyser: none }", "unknown key 'statc_analysis'")]
    [InlineData("kind: organisation", "kind must be repository")]
    [InlineData("test_tiers: [{ name: a, command: x, result: { format: xml, path: r } }]", "format must be one of trx, junit")]
    [InlineData("test_tiers: [{ name: a, targets: [ci] }]", "runs only in ci and needs ci.job")]
    [InlineData("test_tiers: [{ name: a, targets: [cloud], command: x }]", "'cloud' is not sandbox, local or ci")]
    [InlineData("test_tiers: [{ name: a, command: x }, { name: a, command: y }]", "declared twice")]
    [InlineData("static_analysis: { analyser: sarif }", "needs command and sarif_path")]
    public void A_manifest_that_is_wrong_is_refused_not_ignored(string change, string message)
    {
        var text = change.StartsWith("kind:", StringComparison.Ordinal)
            ? "api_version: feature-flow/v1\n" + change + "\n"
            : "api_version: feature-flow/v1\nkind: repository\n" + change + "\n";

        var (_, d) = Read(text);

        Assert.Contains(d, x => x.Blocks && x.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_returns_the_manifest_or_throws_a_format_error_naming_the_problem()
    {
        Assert.Equal(2, RepositoryManifest.Parse(Full, "sha256:x", "f").Tiers.Count);

        var e = Assert.Throws<FormatException>(() => RepositoryManifest.Parse("api_version: feature-flow/v1\nkind: repository\ntestTiers: []\n", "h", "acme/api:feature-flow.yaml"));

        Assert.Contains("acme/api:feature-flow.yaml does not read: unknown key 'testTiers'", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acme/poc-*", "acme/poc-api", true)]
    [InlineData("acme/poc-*", "acme/api", false)]
    [InlineData("acme/*", "acme/api", true)]
    [InlineData("*/api", "acme/api", true)]
    [InlineData("acme/api", "ACME/api", true)]
    [InlineData("acme/api", "acme/api2", false)]
    [InlineData("a*b*c", "a-b-c", true)]
    [InlineData("a*b*c", "a-c-b", false)]
    public void Repository_patterns_match_with_stars(string pattern, string repository, bool matches) =>
        Assert.Equal(matches, RepositoryPatterns.Matches(pattern, repository));

    [Fact]
    public void The_organisation_reads_F1s_engine_may_merge_and_defaults_to_none()
    {
        const string text = """
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            policy:
              merge:
                default_branch: { engine_may_merge: ["acme/poc-*"] }
            """;
        var d = new DiagnosticBag();

        var config = ConfigurationReader.ReadOrganisation(text, "organisation.yaml", d)!;
        var plain = ConfigurationReader.ReadOrganisation("api_version: feature-flow/v1\nkind: organisation\nname: acme\n", "o.yaml", d)!;

        Assert.Empty(d);
        Assert.Equal(["acme/poc-*"], config.Policy.EngineMayMerge);
        Assert.Empty(plain.Policy.EngineMayMerge);
    }
}
