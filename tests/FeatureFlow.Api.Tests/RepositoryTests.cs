using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// Repositories (A16, GitHub only): anyone signed in registers one by <c>owner/name</c> or URL once the code host finds it;
/// a local path waits for E5; the list merges the repositories runs name, marked unregistered.
/// </summary>
public sealed class RepositoryTests : IAsyncDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-api-repos-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = InMemoryForge.Named("repos-" + Guid.NewGuid().ToString("N"));
    readonly SqliteRecordStore records;
    readonly Microsoft.AspNetCore.Builder.WebApplication app;
    readonly Uri address;

    public RepositoryTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        forge.CreateRepository("acme/api", defaultBranch: "trunk");
        forge.CreateRepository("acme/web");
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
                ["artifact_store"] = new Dictionary<string, IProvider> { ["files"] = new FileSystemArtifactStore(Path.Combine(folder, "artifacts")) },
                ["code_host"] = new Dictionary<string, IProvider> { ["memory"] = new InMemoryCodeHost(forge) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file", ["artifact_store"] = "files", ["code_host"] = "memory" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "local", ["viewer"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"));
        app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!) { Authenticators = [new PersonAuthenticator()] }, "http://127.0.0.1:0");
        app.StartAsync().GetAwaiter().GetResult();
        address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    /// <summary>The caller named by <c>X-Person</c>, holding the roles <c>X-Roles</c> lists (none if empty).</summary>
    sealed class PersonAuthenticator : IAuthenticator
    {
        public Task<Caller?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, ApiServices services) =>
            Task.FromResult(context.Request.Headers["X-Person"].ToString() is { Length: > 0 } person
                ? new Caller(person, context.Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries), null, CallerChannels.Api)
                : null);
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    HttpClient As(string person, string roles = "")
    {
        var http = new HttpClient { BaseAddress = address };
        http.DefaultRequestHeaders.Add("X-Person", person);
        http.DefaultRequestHeaders.Add("X-Roles", roles);
        return http;
    }

    static async Task<(HttpStatusCode Status, string Body)> RegisterAsync(HttpClient http, string body)
    {
        using var response = await http.PostAsync("repositories", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("acme/api")]
    [InlineData("https://github.com/acme/api")]
    [InlineData("https://github.com/acme/api.git")]
    public async Task A_caller_with_no_roles_registers_a_github_repository_by_name_or_url_with_its_default_branch(string github)
    {
        var (status, body) = await RegisterAsync(As("pat"), $$"""{ "github": "{{github}}" }""");

        Assert.True(status == HttpStatusCode.Created, body);
        var view = JsonNode.Parse(body)!;
        Assert.Equal(("acme/api", "github", "https://github.com/acme/api", "trunk", true, "pat"),
            (view["name"]!.GetValue<string>(), view["host"]!.GetValue<string>(), view["url"]!.GetValue<string>(), view["default_branch"]!.GetValue<string>(),
                view["registered"]!.GetValue<bool>(), view["registered_by"]!.GetValue<string>()));
        var stored = Assert.Single((await records.QueryAsync<RepositoryRecord>(Queries.Repositories, new Dictionary<string, string>(), Ct)).Unwrap());
        Assert.Equal(("acme/api", "trunk", "pat"), (stored.Name, stored.DefaultBranch, stored.RegisteredBy));
    }

    [Theory]
    [InlineData("""{ "github": "acme/missing" }""", "acme/missing was not found on GitHub")]
    [InlineData("""{ "path": "/home/pat/src/api" }""", "by path is not supported yet (E5)")]
    [InlineData("""{ "github": "https://gitlab.com/acme/api" }""", "github must name a GitHub repository")]
    [InlineData("""{ "github": "acme/api/extra" }""", "github must name a GitHub repository")]
    [InlineData("{}", "github must name a GitHub repository")]
    public async Task An_unknown_repository_a_local_path_or_a_name_that_is_not_githubs_is_refused_and_nothing_is_registered(string request, string reason)
    {
        var (status, body) = await RegisterAsync(As("pat"), request);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, body);
        Assert.Empty((await records.QueryAsync<RepositoryRecord>(Queries.Repositories, new Dictionary<string, string>(), Ct)).Unwrap());
    }

    [Fact]
    public async Task A_repository_registered_already_is_refused_by_name_or_url_in_any_case()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(As("pat"), """{ "github": "acme/api" }""")).Status);

        var (status, body) = await RegisterAsync(As("sam"), """{ "github": "https://github.com/ACME/API" }""");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("acme/api is already registered, by pat", body);
    }

    [Fact]
    public async Task The_list_merges_the_repositories_named_by_runs_the_caller_views_marked_unregistered()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(As("pat"), """{ "github": "acme/api" }""")).Status);
        await records.AppendAsync(new RecordBatch([new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", ["acme/web", "acme/api"], false, DateTimeOffset.UtcNow, null)]), Ct);

        async Task<List<(string, bool, string?)>> ListAsync(HttpClient http) =>
            JsonNode.Parse(await http.GetStringAsync("repositories", Ct))!["repositories"]!.AsArray()
                .Select(r => (r!["name"]!.GetValue<string>(), r["registered"]!.GetValue<bool>(), r["registered_by"]?.GetValue<string>())).ToList();

        Assert.Equal([("acme/api", true, "pat"), ("acme/web", false, null)], await ListAsync(As("viewer", "viewer")));
        Assert.Equal([("acme/api", true, "pat")], await ListAsync(As("stranger")));
    }
}
