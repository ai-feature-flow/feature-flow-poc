using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// OIDC bearer tokens (docs/02 section 7) against an issuer in this process: discovery, its keys, audience and expiry, the
/// groups claim giving people roles, beside the local token. No real identity provider.
/// </summary>
public sealed class OidcTests : IAsyncDisposable
{
    readonly WebApplication issuer;
    readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "k1" };
    readonly RsaSecurityKey other = new(RSA.Create(2048)) { KeyId = "k1" };
    readonly RsaSecurityKey rotated = new(RSA.Create(2048)) { KeyId = "k2" };
    RsaSecurityKey published;
    readonly string issuerUrl;
    string? metadataIssuer;
    ApiWorld? world;

    public OidcTests()
    {
        published = key;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        issuer = builder.Build();
        issuer.MapGet("/.well-known/openid-configuration", (HttpContext c) => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = metadataIssuer ?? issuerUrl!,
            ["jwks_uri"] = issuerUrl + "/jwks",
        }));
        issuer.MapGet("/jwks", () => Results.Text(JsonSerializer.Serialize(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(PublicOf(published!)) } },
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), "application/json"));
        issuer.StartAsync().GetAwaiter().GetResult();
        issuerUrl = issuer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    static RsaSecurityKey PublicOf(RsaSecurityKey key) => new(key.Rsa.ExportParameters(false)) { KeyId = key.KeyId };

    public async ValueTask DisposeAsync()
    {
        if (world is not null)
            await world.DisposeAsync();
        await issuer.DisposeAsync();
    }

    async Task<ApiWorld> WorldAsync(string? tokenRef = null)
    {
        var auth = new ApiAuthConfig("oidc", tokenRef, "local", ["viewer", "approver", "steerer", "admin"]) { Issuer = issuerUrl, Audience = "feature-flow" };
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([new ApprovalRule("*", ["approver"], 2, false, new Cap<bool>(false, false))], null, null, null, null), "")
        {
            Roles = new Dictionary<string, PeopleRoleConfig> { ["viewer"] = new(["eng"]), ["steerer"] = new(["leads"]), ["approver"] = new(["leads"]) },
        };
        world = new ApiWorld(organisation, ApiAuthentication.For(auth), auth);
        await world.RunAsync("r1", "acme/api");
        await world.WaitAsync("r1", "w1", "decision", "ticket.spec_approval");
        return world;
    }

    string Token(string? subject = "ana", string[]? groups = null, string audience = "feature-flow", string? issuedBy = null, DateTime? expires = null,
        RsaSecurityKey? signedWith = null)
    {
        var claims = new Dictionary<string, object> { ["groups"] = groups ?? ["leads"] };
        if (subject is not null)
            claims["sub"] = subject;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuedBy ?? issuerUrl,
            Audience = audience,
            Claims = claims,
            IssuedAt = (expires ?? now.AddMinutes(10)).AddMinutes(-20),
            NotBefore = (expires ?? now.AddMinutes(10)).AddMinutes(-20),
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(signedWith ?? key, SecurityAlgorithms.RsaSha256),
        });
    }

    HttpClient With(string token)
    {
        var http = new HttpClient { BaseAddress = world!.Address };
        http.DefaultRequestHeaders.Authorization = ApiWorld.Bearer(token);
        return http;
    }

    async Task<HttpStatusCode> AbortAsync(string token) =>
        (await ApiWorld.PostAsync(With(token), "runs/r1/abort", """{ "reason": "stop", "actor": "mallory" }""")).Status;

    [Fact]
    public async Task A_valid_token_authenticates_its_subject_with_roles_from_its_groups()
    {
        await WorldAsync();

        Assert.Equal(HttpStatusCode.OK, await AbortAsync(Token()));
        Assert.Equal("ana", ((AbortArgs)Assert.Single(world!.Temporal.Updates).Argument!).Actor);
        // A viewer may read but not abort; a person in no mapped group may not even read.
        Assert.Equal(HttpStatusCode.Forbidden, await AbortAsync(Token(groups: ["eng"])));
        using var read = await With(Token(groups: ["eng"])).GetAsync("runs/r1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var unmapped = await With(Token(groups: ["sales"])).GetAsync("runs/r1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
    }

    [Fact]
    public async Task An_expired_misaddressed_foreign_or_unsigned_token_is_refused()
    {
        await WorldAsync();
        Assert.Equal(HttpStatusCode.OK, await AbortAsync(Token()));

        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(expires: DateTime.UtcNow.AddMinutes(-5))));
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(audience: "another-service")));
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(issuedBy: "https://evil.example")));
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(signedWith: other)));
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuerUrl, Audience = "feature-flow", Expires = DateTime.UtcNow.AddMinutes(5), Claims = new Dictionary<string, object> { ["sub"] = "ana", ["groups"] = "leads" },
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(unsigned));
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(subject: null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(subject: "policy")));
        Assert.Single(world!.Temporal.Updates);
    }

    [Fact]
    public async Task The_local_token_works_beside_oidc_only_where_configured()
    {
        await WorldAsync(tokenRef: "secrets/api_token");
        Assert.Equal(HttpStatusCode.OK, await AbortAsync("right"));
        Assert.Equal("local", ((AbortArgs)Assert.Single(world!.Temporal.Updates).Argument!).Actor);
        await world.DisposeAsync();

        await WorldAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync("right"));
    }

    [Fact]
    public async Task The_local_token_is_not_a_second_person_beside_oidc()
    {
        await WorldAsync(tokenRef: "secrets/api_token");
        const string answer = """{ "wait_id": "w1", "outcome": "approved" }""";

        Assert.Equal(HttpStatusCode.Accepted, (await ApiWorld.PostAsync(With(Token("ann")), "runs/r1/answers", answer)).Status);
        var (local, text) = await ApiWorld.PostAsync(With("right"), "runs/r1/answers", answer);
        Assert.Equal((HttpStatusCode.Forbidden, true), (local, text.Contains("not with the local token", StringComparison.Ordinal)));
        Assert.Empty(world!.Temporal.Updates);
        Assert.Equal(HttpStatusCode.OK, (await ApiWorld.PostAsync(With(Token("bob")), "runs/r1/answers", answer)).Status);
        Assert.Equal("ann, bob", ((AnswerArgs)Assert.Single(world.Temporal.Updates).Argument!).Actor);
    }

    [Fact]
    public async Task A_token_signed_by_a_rotated_key_is_accepted_once_the_keys_are_read_again()
    {
        await WorldAsync();
        Assert.Equal(HttpStatusCode.OK, await AbortAsync(Token()));
        published = rotated;

        // The cached keys lack k2: refused, and the keys are read again for later requests.
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(signedWith: rotated)));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var status = HttpStatusCode.Unauthorized;
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            status = await AbortAsync(Token(signedWith: rotated));
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_token_must_name_the_configured_issuer_whatever_the_metadata_says()
    {
        metadataIssuer = "https://other.example";
        await WorldAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await AbortAsync(Token(issuedBy: "https://other.example")));
    }
}
