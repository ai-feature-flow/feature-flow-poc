using System.Net;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// GitHub recorded as routes: each request is answered by the first route whose method and path (with query) match,
/// so a test states exactly the responses GitHub documents. A request with no route fails the test.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    readonly List<(HttpMethod Method, string Path, Func<string, HttpResponseMessage> Respond)> routes = [];

    public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

    /// <summary>Answers <paramref name="method"/> on a path starting with <paramref name="path"/> (after <c>/repos/o/r</c> when it starts with <c>~</c>).</summary>
    public FakeGitHub On(HttpMethod method, string path, int status, string body = "", IReadOnlyDictionary<string, string>? headers = null) =>
        On(method, path, _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in headers ?? new Dictionary<string, string>())
                if (k == "Location")
                    response.Headers.Location = new Uri(v);
                else
                    response.Headers.TryAddWithoutValidation(k, v);
            return response;
        });

    public FakeGitHub On(HttpMethod method, string path, Func<string, HttpResponseMessage> respond)
    {
        routes.Add((method, path.StartsWith('~') ? "/repos/o/r" + path[1..] : path, respond));
        return this;
    }

    public FakeGitHub Bytes(string url, byte[] content)
    {
        routes.Add((HttpMethod.Get, url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        return this;
    }

    public IEnumerable<(HttpMethod Method, string Path, string Body)> Called(HttpMethod method, string path) =>
        Calls.Where(c => c.Method == method && c.Path.StartsWith(path.StartsWith('~') ? "/repos/o/r" + path[1..] : path, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.Host == "api.example" ? request.RequestUri.PathAndQuery : request.RequestUri.ToString();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, path, body));
        foreach (var (method, prefix, respond) in routes)
            if (method == request.Method && path.StartsWith(prefix, StringComparison.Ordinal))
                return respond(body);
        throw new InvalidOperationException($"no route for {request.Method} {path}");
    }

    /// <summary>A broker that issues one fixed token and counts what it was asked for.</summary>
    public sealed class Broker : ICredentialBroker
    {
        public List<CredentialScope> Issued { get; } = [];

        public ProviderDescription Describe() => new("credential_broker", "test", "0", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl, CancellationToken cancellationToken)
        {
            Issued.Add(scope);
            return Task.FromResult(ProviderResult<Credential>.Success(new Credential(targetSystem, scope, "ghs_test", DateTimeOffset.UtcNow.AddHours(1), actor)));
        }

        public Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken cancellationToken) =>
            IssueAsync(credential.Actor, credential.TargetSystem, credential.Scope, TimeSpan.FromHours(1), cancellationToken);
    }

    public GitHubApi Api(Broker? broker = null)
    {
        var b = broker ?? new Broker();
        return new GitHubApi(() => b, this, "https://api.example");
    }
}
