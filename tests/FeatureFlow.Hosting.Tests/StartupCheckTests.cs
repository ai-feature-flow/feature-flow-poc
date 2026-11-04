using FeatureFlow.Model;

namespace FeatureFlow.Hosting.Tests;

/// <summary>The startup checks across the installation and organisation layers (docs/07 section 7), each naming the layer, key and reason.</summary>
public sealed class StartupCheckTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-startup-" + Guid.NewGuid().ToString("N"));

    public StartupCheckTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static string Std => Path.Combine(FindRoot(), "library", "std");

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "FeatureFlow.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    List<string> Errors(string auth, string organisation)
    {
        File.WriteAllText(Path.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            providers:
              agent_runtime: { default: scripted, instances: { scripted: { script: s.yaml } } }
            api:
              auth: {{auth}}
            """);
        File.WriteAllText(Path.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries: [{ name: std, source: directory, path: "{{Std}}" }]
            {{organisation}}
            """);
        try
        {
            FeatureFlowConfiguration.Load(Path.Combine(folder, "installation.yaml"), Path.Combine(folder, "organisation.yaml"));
            return [];
        }
        catch (InvalidOperationException e)
        {
            return e.Message.Split('\n').Skip(1).ToList();
        }
    }

    const string LocalToken = "{ mode: local_token, token_ref: secrets/t }";

    [Fact]
    public void A_consistent_pair_of_files_loads()
    {
        Assert.Empty(Errors(LocalToken, "role_mapping: { planner: { agent_runtime: scripted } }\nnotifications: { events: [failed] }"));
        Assert.Empty(Errors("{ mode: oidc, issuer: \"https://idp.example\", audience: ff }", "roles: { viewer: { idp_groups: [eng] } }"));
    }

    [Theory]
    [InlineData(LocalToken, "role_mapping: { planner: { agent_runtime: claude_code } }", "organisation: role_mapping.planner.agent_runtime 'claude_code' is not an agent_runtime instance")]
    [InlineData(LocalToken, "notifications: { events: [failed, exploded] }", "organisation: notifications: 'exploded' is not a notification event")]
    [InlineData(LocalToken, "notifications: { routing: { stuk: { channel: \"#ops\" } } }", "organisation: notifications: 'stuk' is not a notification event")]
    [InlineData("{ mode: oidc, issuer: \"https://idp.example\", audience: ff }", "", "organisation: roles: api.auth.mode is oidc")]
    public void A_failure_names_the_layer_the_key_and_the_reason(string auth, string organisation, string message) =>
        Assert.Contains(Errors(auth, organisation), e => e.Contains(message, StringComparison.Ordinal));

    [Fact]
    public void A_library_that_does_not_open_stops_startup()
    {
        var missing = Path.Combine(folder, "no-such-library");
        File.WriteAllText(Path.Combine(folder, "installation.yaml"), "api_version: feature-flow/v1\nkind: installation\napi:\n  auth: " + LocalToken + "\n");
        File.WriteAllText(Path.Combine(folder, "organisation.yaml"), $"api_version: feature-flow/v1\nkind: organisation\nname: a\nlibraries: [{{ name: mine, source: directory, path: \"{missing}\" }}]\n");
        var e = Assert.Throws<InvalidOperationException>(() => FeatureFlowConfiguration.Load(Path.Combine(folder, "installation.yaml"), Path.Combine(folder, "organisation.yaml")));
        Assert.Contains("organisation: libraries.mine: the directory", e.Message, StringComparison.Ordinal);
    }
}
