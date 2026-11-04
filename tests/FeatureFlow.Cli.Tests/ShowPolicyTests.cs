using FeatureFlow.Cli;

namespace FeatureFlow.Cli.Tests;

/// <summary><c>ff show</c> with the effective policy view (docs/05 section 7), against an organisation file and a team's repositories.</summary>
public sealed class ShowPolicyTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-show-" + Guid.NewGuid().ToString("N"));

    public ShowPolicyTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static readonly string Template = Path.Combine(FindRoot(), "library", "std", "templates", "ticket_delivery.yaml");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    string Organisation(string text)
    {
        var path = Path.Combine(folder, "organisation.yaml");
        File.WriteAllText(path, "api_version: feature-flow/v1\nkind: organisation\nname: acme\n" + text);
        return path;
    }

    [Fact]
    public void Without_an_organisation_the_view_shows_the_libraries_defaults()
    {
        var (code, output, error) = Run("show", Template);

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("approve_spec auto-resolution: resolves 'approved' at once, actor policy", output, StringComparison.Ordinal);
        Assert.Contains("from: library default_approval of ticket.spec_approval", output, StringComparison.Ordinal);
        Assert.Contains("no --organisation given", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_follows_the_organisation_and_the_team_owning_the_repositories_given()
    {
        var organisation = Organisation("""
            policy:
              approval: [{ match: "*", roles: [approver], auto: true }]
            teams:
              payments:
                repositories: ["acme/payments-*"]
                policy: { approval: [{ match: "ticket.*", min_count: 2 }] }
            """);

        var (_, outside, _) = Run("show", Template, "--organisation", organisation);
        var (code, inside, error) = Run("show", Template, "--organisation", organisation, "--repo", "acme/payments-api");

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("approve_spec who answers: approver, 1 person, console or CLI only | asks: ticket.spec_approval | cap: - | from: approval rule '*'", outside, StringComparison.Ordinal);
        Assert.Contains("approve_spec who answers: approver, 2 people, console or CLI only | asks: ticket.spec_approval | cap: - | from: approval rule '*', min_count from team payments's rule 'ticket.*'", inside, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_organisation_file_is_refused_with_its_errors()
    {
        var (code, _, error) = Run("show", Template, "--organisation", Organisation("polcy: {}\n"));

        Assert.Equal(2, code);
        Assert.Contains("unknown key 'polcy'", error, StringComparison.Ordinal);
    }
}
