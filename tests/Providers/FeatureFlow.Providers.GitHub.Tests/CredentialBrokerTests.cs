using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The GitHub App credential broker (docs/06 section 3.13), ported from Spike 2: it mints the App's JWT, asks for
/// an installation token scoped as requested, and refuses, never narrows, a request it may not grant.
/// </summary>
public sealed class CredentialBrokerTests
{
    static readonly RSA Key = RSA.Create(2048);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class GitHub : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var body = request.RequestUri!.AbsolutePath == "/app"
                ? """{"id":5507595,"name":"feature-flow"}"""
                : $$"""{"token":"ghs_secret","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""";
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body) };
        }
    }

    static (AppCredentialBroker Broker, GitHub Api) Broker()
    {
        var api = new GitHub();
        var broker = new AppCredentialBroker("5507595", "176108462", _ => Task.FromResult(ProviderResult<string>.Success(Key.ExportRSAPrivateKeyPem())),
            ["ai-feature-flow/feature-flow-example"], api, "https://api.example");
        return (broker, api);
    }

    [Fact]
    public async Task The_engine_gets_a_token_scoped_to_one_repository_and_its_permissions()
    {
        var (broker, api) = Broker();
        var issued = await broker.IssueAsync(Actor.Engine, "github", CredentialScope.Write("ai-feature-flow/feature-flow-example"), TimeSpan.FromMinutes(30), Ct);

        Assert.True(issued.Ok, issued.Error?.Message);
        var (request, body) = Assert.Single(api.Calls);
        Assert.Equal("/app/installations/176108462/access_tokens", request.RequestUri!.AbsolutePath);
        var sent = JsonNode.Parse(body)!;
        Assert.Equal(["feature-flow-example"], sent["repositories"]!.AsArray().Select(r => r!.ToString()));
        Assert.Equal("write", sent["permissions"]!["contents"]!.ToString());
        // The credential ends when asked, not when GitHub's hour ends, and never shows its secret.
        Assert.True(issued.Value!.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(31));
        Assert.DoesNotContain("ghs_secret", issued.Value.ToString());
        // The JWT is the App's: RS256 over the header and payload, issued by the App id.
        var jwt = request.Headers.Authorization!.Parameter!.Split('.');
        Assert.True(Key.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), FromBase64Url(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(5507595, JsonNode.Parse(FromBase64Url(jwt[1]))!["iss"]!.GetValue<long>());
    }

    public static TheoryData<string, string[], string, string, double> Refused => new()
    {
        { "engine", ["ai-feature-flow/other"], "contents", "read", 30 },
        { "agent", ["ai-feature-flow/feature-flow-example"], "contents", "write", 30 },
        { "engine", ["ai-feature-flow/feature-flow-example"], "administration", "write", 30 },
        { "engine", ["ai-feature-flow/feature-flow-example"], "contents", "write", 120 },
        { "model_proxy", ["ai-feature-flow/feature-flow-example"], "contents", "read", 30 },
        // F5: an agent has no tracker identity, not even to read.
        { "agent", ["ai-feature-flow/feature-flow-example"], "issues", "read", 30 },
    };

    /// <summary>F5: no agent can push and nothing can change branch protection; a refusal makes no call to GitHub.</summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_request_wider_than_the_broker_may_grant_is_refused_before_any_call(string actor, string[] repositories, string permission, string access,
        double minutes)
    {
        var (broker, api) = Broker();
        var scope = new CredentialScope(repositories, new Dictionary<string, string> { [permission] = access });
        var issued = await broker.IssueAsync(new Actor(actor, "x"), "github", scope, TimeSpan.FromMinutes(minutes), Ct);

        Assert.False(issued.Ok);
        Assert.Equal("rejected", issued.Error!.Reason);
        Assert.Equal(ProviderErrorClass.Work, issued.Error.Class);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task The_engine_may_write_issues_for_the_tracker()
    {
        var (broker, _) = Broker();
        var scope = new CredentialScope(["ai-feature-flow/feature-flow-example"], new Dictionary<string, string> { ["issues"] = "write", ["metadata"] = "read" });
        Assert.True((await broker.IssueAsync(Actor.Engine, "github", scope, TimeSpan.FromMinutes(30), Ct)).Ok);
    }

    [Fact]
    public async Task An_agent_may_read_its_repository()
    {
        var (broker, _) = Broker();
        Assert.True((await broker.IssueAsync(new Actor("agent", "run-1/implement/1"), "github", CredentialScope.Read("ai-feature-flow/feature-flow-example"),
            TimeSpan.FromMinutes(30), Ct)).Ok);
    }

    static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
