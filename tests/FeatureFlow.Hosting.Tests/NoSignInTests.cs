using FeatureFlow.Api;
using FeatureFlow.Model;

namespace FeatureFlow.Hosting.Tests;

/// <summary>A4 T2, LOG 457: <c>api.auth.mode: none</c> starts a host only when its API listens on loopback alone.</summary>
public sealed class NoSignInTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-nosignin-" + Guid.NewGuid().ToString("N"));

    public NoSignInTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    const string Installation = """
        api_version: feature-flow/v1
        kind: installation
        development: true
        temporal: { address: "127.0.0.1:1" }
        api:
          auth: { mode: none, actor: ana }
        """;

    static FeatureFlowConfiguration Configuration()
    {
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation(Installation, "/tmp/installation.yaml", d)!;
        Assert.False(d.HasErrors, string.Join("\n", d));
        return new FeatureFlowConfiguration(installation, new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), "/tmp/organisation.yaml"));
    }

    [Theory]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://[::]:0")]
    [InlineData("http://*:0")]
    [InlineData("http://192.168.1.10:0")]
    [InlineData("http://example.com:0")]
    public async Task A_host_with_no_sign_in_refuses_to_start_on_an_address_other_than_loopback(string url)
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => FeatureFlowHost.StartAsync(Configuration(), [], false, false, url));

        Assert.Contains("api.auth.mode is none", e.Message, StringComparison.Ordinal);
        Assert.Contains($"'{url}'", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://[::1]:0")]
    public async Task A_host_with_no_sign_in_gets_past_the_loopback_check_on_loopback(string url)
    {
        // No Temporal listens on 127.0.0.1:1: the host stops there, after the check has let the address through.
        var e = await Record.ExceptionAsync(() => FeatureFlowHost.StartAsync(Configuration(), [], false, false, url));

        Assert.NotNull(e);
        Assert.DoesNotContain("api.auth.mode is none", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_no_sign_in_refuses_to_start_when_FF_API_LISTEN_is_not_loopback()
    {
        File.WriteAllText(Path.Combine(folder, "installation.yaml"), Installation);
        File.WriteAllText(Path.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries: [{ name: std, source: directory, path: "{{Path.Combine(Repository.Root, "library", "std")}}" }]
            """);
        string[] args = ["--installation", Path.Combine(folder, "installation.yaml"), "--organisation", Path.Combine(folder, "organisation.yaml")];
        var before = Environment.GetEnvironmentVariable("FF_API_LISTEN");
        Environment.SetEnvironmentVariable("FF_API_LISTEN", "http://0.0.0.0:8480");
        try
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => HostArguments.RunAsync(args, [], false, false, true));

            Assert.Contains("api.auth.mode is none, so the API must listen only on loopback", e.Message, StringComparison.Ordinal);
            Assert.Contains("'http://0.0.0.0:8480'", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FF_API_LISTEN", before);
        }
    }

    [Theory]
    [InlineData("http://localhost:8480", true)]
    [InlineData("http://LocalHost:8480", true)]
    [InlineData("http://127.0.0.1:8480", true)]
    [InlineData("http://127.0.0.2:8480", true)]
    [InlineData("https://127.255.255.254", true)]
    [InlineData("http://[::1]:8480", true)]
    [InlineData("http://0.0.0.0:8480", false)]
    [InlineData("http://[::]:8480", false)]
    [InlineData("http://*:8480", false)]
    [InlineData("http://+:8480", false)]
    [InlineData("http://[::ffff:127.0.0.1]:8480", false)]
    [InlineData("http://128.0.0.1:8480", false)]
    [InlineData("http://localhost.example.com:8480", false)]
    [InlineData("http://unix:/tmp/ff.sock", false)]
    [InlineData("not a url", false)]
    public void The_loopback_rule(string url, bool loopback) =>
        Assert.Equal(loopback ? null : url, ApiApp.LoopbackOnly(["http://127.0.0.1:1", url]));

    [Fact]
    public void Only_mode_none_is_held_to_loopback()
    {
        var token = new ApiAuthConfig("local_token", "secrets/t", "local", ["admin"]);

        ApiApp.RefuseUnlessLoopback(token, ["http://0.0.0.0:8480"]);
        Assert.Throws<InvalidOperationException>(() => ApiApp.RefuseUnlessLoopback(token with { Mode = "none", TokenRef = null }, ["http://0.0.0.0:8480"]));
    }
}
