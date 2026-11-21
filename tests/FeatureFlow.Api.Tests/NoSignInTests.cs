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
        var e = Assert.Throws<StartupRefusal>(() => ApiApp.Build(Services(), "http://127.0.0.1:0", "http://0.0.0.0:0"));

        Assert.Contains("but it would listen on 'http://0.0.0.0:0'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_API_stops_and_refuses_when_it_binds_an_address_other_than_loopback()
    {
        await using var app = ApiApp.Build(Services(), "http://127.0.0.1:0");
        // As Kestrel's own configuration can: an address the configured URLs never named.
        app.Urls.Add("http://0.0.0.0:0");

        var e = await Assert.ThrowsAsync<StartupRefusal>(() => ApiApp.StartCheckedAsync(app, None));

        Assert.Contains("api.auth.mode is none", e.Message, StringComparison.Ordinal);
        Assert.Contains("'http://0.0.0.0:", e.Message, StringComparison.Ordinal);
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    /// <summary>A request as a page of another site, or DNS rebinding, can make a browser send it (review R1, LOG 469).</summary>
    public static TheoryData<string, string> CrossSite => new()
    {
        { "host", "Host 'evil.example" },
        { "text/plain", "must have Content-Type application/json" },
        { "cross-site", "Sec-Fetch-Site is 'cross-site'" },
        { "same-site", "Sec-Fetch-Site is 'same-site'" },
    };

    static HttpRequestMessage Abort(string kind)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "runs/r-missing/abort")
        {
            Content = new StringContent("""{"reason":"x"}""", System.Text.Encoding.UTF8, kind == "text/plain" ? "text/plain" : "application/json"),
        };
        if (kind == "host")
            request.Headers.Host = "evil.example";
        if (kind is "cross-site" or "same-site")
            request.Headers.Add("Sec-Fetch-Site", kind);
        return request;
    }

    [Theory]
    [MemberData(nameof(CrossSite))]
    public async Task With_no_sign_in_a_request_a_page_of_another_site_could_make_is_refused(string kind, string reason)
    {
        await using var world = new ApiWorld(auth: None, authenticators: ApiAuthentication.For(None));
        using var http = new HttpClient { BaseAddress = world.Address };

        using var response = await http.SendAsync(Abort(kind), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = JsonSerializer.Deserialize<ErrorResponse>(await response.Content.ReadAsStringAsync(Ct), ApiJson.Options)!;
        Assert.Contains("api.auth.mode is none", error.Error, StringComparison.Ordinal);
        Assert.Contains(reason, error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_sign_in_the_consoles_own_requests_are_answered_and_the_gateway_and_console_files_are_not_checked()
    {
        await using var world = new ApiWorld(auth: None, authenticators: ApiAuthentication.For(None));
        using var http = new HttpClient { BaseAddress = world.Address };

        // Same-origin JSON, as the console sends it, on a loopback name and another port (a port forward).
        using var abort = Abort("json");
        abort.Headers.Add("Sec-Fetch-Site", "same-origin");
        abort.Headers.Host = "LOCALHOST:9999";
        using var answered = await http.SendAsync(abort, Ct);
        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        foreach (var (host, site) in new[] { ("[::1]", "none"), ($"127.0.0.2:{world.Address.Port}", "") })
        {
            using var me = new HttpRequestMessage(HttpMethod.Get, "me");
            me.Headers.Host = host;
            if (site.Length > 0)
                me.Headers.Add("Sec-Fetch-Site", site);
            using var read = await http.SendAsync(me, Ct);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        // The chat gateway authenticates its own signed requests, form-encoded and through whatever proxy; the console's
        // files are public.
        using var chat = new HttpRequestMessage(HttpMethod.Post, "inbound/chat/slack") { Content = new FormUrlEncodedContent([new("payload", "{}")]) };
        chat.Headers.Host = "ff.example";
        chat.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var gateway = await http.SendAsync(chat, Ct);
        Assert.Equal(HttpStatusCode.NotFound, gateway.StatusCode);
        Assert.Contains("no provider receives slack answers", await gateway.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var page = new HttpRequestMessage(HttpMethod.Get, "console");
        page.Headers.Host = "evil.example";
        using var console = await http.SendAsync(page, Ct);
        Assert.NotEqual(HttpStatusCode.Forbidden, console.StatusCode);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("text/plain")]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task With_a_token_the_same_requests_are_not_checked(string kind)
    {
        var token = new ApiAuthConfig("local_token", "secrets/api_token", "ana", ["admin"]);
        await using var world = new ApiWorld(auth: token, authenticators: ApiAuthentication.For(token));
        using var http = new HttpClient { BaseAddress = world.Address };
        http.DefaultRequestHeaders.Authorization = ApiWorld.Bearer("right");

        using var response = await http.SendAsync(Abort(kind), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Review R7: an address Kestrel's own configuration bound is answered from the moment the API starts until
    /// <see cref="ApiApp.StartCheckedAsync"/> stops it, so each request's local address is checked too. Seen as the
    /// predicate: a request to this machine's own non-loopback address cannot be made where a firewall drops it.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.8.7", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.0.7", false)]
    [InlineData("fe80::1", false)]
    [InlineData(null, false)]
    public void With_no_sign_in_only_a_connection_to_a_loopback_address_is_answered(string? local, bool answered)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.LocalIpAddress = local is null ? null : IPAddress.Parse(local);
        context.Request.Method = "GET";
        context.Request.Host = new("127.0.0.1", 8480);

        var refusal = NoSignInAuthenticator.Refusal(context);

        Assert.Equal(answered, refusal is null);
        if (!answered)
            Assert.Contains("is not a loopback address", refusal, StringComparison.Ordinal);
    }
}
