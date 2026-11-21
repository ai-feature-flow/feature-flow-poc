using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>A4 T2, LOG 457: <c>api.auth.mode: none</c> answers every request as the configured actor, on loopback only.</summary>
public sealed class NoSignInTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ApiAuthConfig None = new("none", null, "ana", ["approver"]);

    [Fact]
    public async Task A_request_without_a_token_is_the_configured_actor_with_its_roles()
    {
        await using var world = new ApiWorld(auth: None, authenticators: ApiAuthentication.For(None));
        using var http = new HttpClient { BaseAddress = world.Address };
        http.DefaultRequestHeaders.Authorization = ApiWorld.Bearer("anything at all");

        foreach (var authorised in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "me");
            if (!authorised)
                request.Headers.Authorization = null;
            using var response = await http.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var me = JsonSerializer.Deserialize<MeResponse>(await response.Content.ReadAsStringAsync(Ct), ApiJson.Options)!;
            Assert.Equal(("ana", "api", null), (me.Actor, me.Channel, me.Groups));
            Assert.Equal(["approver", "viewer"], me.Roles.Order());
        }
    }

    static ApiServices Services()
    {
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(), None, 3, 10, "/tmp/installation.yaml");
        return new ApiServices(new ProviderSet(new Dictionary<string, IReadOnlyDictionary<string, IProvider>>(), new Dictionary<string, string>(), new Dictionary<string, string>()),
            installation, new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), "/tmp/organisation.yaml"), new FakeTemporal().Client);
    }

    [Fact]
    public void The_API_is_not_built_on_a_configured_address_other_than_loopback()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ApiApp.Build(Services(), "http://127.0.0.1:0", "http://0.0.0.0:0"));

        Assert.Contains("but it would listen on 'http://0.0.0.0:0'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_API_stops_and_refuses_when_it_binds_an_address_other_than_loopback()
    {
        await using var app = ApiApp.Build(Services(), "http://127.0.0.1:0");
        // As Kestrel's own configuration can: an address the configured URLs never named.
        app.Urls.Add("http://0.0.0.0:0");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiApp.StartCheckedAsync(app, None));

        Assert.Contains("api.auth.mode is none", e.Message, StringComparison.Ordinal);
        Assert.Contains("'http://0.0.0.0:", e.Message, StringComparison.Ordinal);
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }
}
