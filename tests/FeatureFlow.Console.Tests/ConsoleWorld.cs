using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The API in process on a real loopback port, as a host builds it, over the Local providers (SQLite records and
/// file-system artifacts in a temporary folder; the in-memory store refuses waits) and <see cref="FakeEngine"/> in place
/// of Temporal (LOG 461). The organisation's libraries are <c>library/std</c> and the M7 scenario library <c>m7s</c>.
/// The change feed polls every 100 ms of real time, so <c>GET /events</c> delivers on its own.
/// </summary>
public sealed class ConsoleWorld : IAsyncDisposable
{
    /// <summary>The installation's API token under <c>local_token</c>.</summary>
    public const string Token = "right";

    /// <summary>The actor every request is under <c>none</c>, and the local token's.</summary>
    public const string Actor = "local";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly LibrarySet Libraries = new(
        Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!,
        Library.Library.Open(Path.Combine(Repository.Root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m7s"), new DiagnosticBag())!);

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-cw-" + Guid.NewGuid().ToString("N"));
    readonly ApiServices services;
    WebApplication app;

    /// <param name="authMode"><c>none</c> (the default: no sign-in) or <c>local_token</c> (<see cref="Token"/>).</param>
    /// <param name="approval">
    /// The organisation's approval rules. By default approvers answer <c>m7s.*</c>: the m7s library declares no roles for its
    /// decision types, so without a rule no one may answer them.
    /// </param>
    /// <param name="overrides">The organisation's <c>policy.overrides</c>: the roles for an override key in place of its default.</param>
    /// <param name="roles">The roles the caller holds; by default every role (viewer, approver, steerer, admin).</param>
    public ConsoleWorld(string authMode = "none", IReadOnlyList<ApprovalRule>? approval = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides = null, IReadOnlyList<string>? roles = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), $$"""{ "api_token": "{{Token}}" }""");
        Records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var artifacts = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        Engine = new FakeEngine(Records, artifacts, Libraries);
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = Records },
                ["artifact_store"] = new Dictionary<string, IProvider> { ["fs"] = artifacts },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
                ["code_host"] = new Dictionary<string, IProvider> { ["memory"] = new InMemoryCodeHost(Forge) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["artifact_store"] = "fs", ["secrets"] = "file", ["code_host"] = "memory" },
            new Dictionary<string, string>());
        roles ??= ["viewer", "approver", "steerer", "admin"];
        Auth = authMode switch
        {
            "none" => new ApiAuthConfig("none", null, Actor, roles),
            "local_token" => new ApiAuthConfig("local_token", "secrets/api_token", Actor, roles),
            _ => throw new ArgumentException($"no such mode {authMode}", nameof(authMode)),
        };
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(), Auth, 3, 10,
            Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t",
            [
                new LibrarySourceConfig("std", "directory", Path.Combine(Repository.Root, "library", "std")),
                new LibrarySourceConfig("m7s", "directory", Path.Combine(Repository.Root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m7s")),
            ],
            new PolicyConfig(approval ?? [new ApprovalRule("m7s.*", [PeopleRoles.Approver], 1, false, new Cap<bool>(false, false))], null, null, null, null)
            {
                Overrides = overrides ?? new Dictionary<string, IReadOnlyList<string>>(),
            },
            Path.Combine(folder, "organisation.yaml"));
        // As FeatureFlow.Hosting composes it: the configured authenticators and the organisation's roles.
        services = new ApiServices(providers, installation, organisation, Engine.Client)
        {
            Authenticators = ApiAuthentication.For(Auth),
            PeopleRoles = new OrganisationRoles(organisation),
            EventPoll = TimeSpan.FromMilliseconds(100),
        };
        app = Start("http://127.0.0.1:0");
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    WebApplication Start(string url)
    {
        var started = ApiApp.Build(services, url);
        started.StartAsync(Ct).GetAwaiter().GetResult();
        return started;
    }

    public ApiAuthConfig Auth { get; }

    public IRecordStoreProvider Records { get; }

    /// <summary>The code host's repositories (an in-memory GitHub), so a repository can be registered (<c>POST /repositories</c>).</summary>
    public InMemoryForge Forge { get; } = InMemoryForge.Named("console-" + Guid.NewGuid().ToString("N"));

    public FakeEngine Engine { get; }

    /// <summary>The API's address, such as <c>http://127.0.0.1:53211/</c>.</summary>
    public Uri Address { get; }

    /// <summary>The console's address: what an installation sets as Slack's <c>console_url</c>.</summary>
    public string ConsoleUrl => new Uri(Address, "console").ToString();

    /// <summary>A client of the API as <see cref="Actor"/>, with the token where the API needs one.</summary>
    public HttpClient Http()
    {
        var http = new HttpClient { BaseAddress = Address };
        if (Auth.Mode != "none")
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }

    /// <summary>Starts a run through the API (<c>POST /runs</c>), so <see cref="FakeEngine"/> writes what start writes; returns its id.</summary>
    public async Task<string> StartRunAsync(string template = "m7s:templates/decide", IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyList<string>? repositories = null)
    {
        using var http = Http();
        using var response = await http.PostAsync("runs", new StringContent(JsonSerializer.Serialize(new StartRunRequest(template, repositories, parameters), ApiJson.Options), Encoding.UTF8, "application/json"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<StartRunResponse>(body, ApiJson.Options)!.RunId;
    }

    /// <summary>A run recorded with one open wait of <paramref name="kind"/>, written directly (no plan); returns the wait's id.</summary>
    public async Task<string> SeedRunWithWaitAsync(string runId, string kind = "gate", string template = "m7s:templates/decide", string prompt = "Go ahead?",
        IReadOnlyList<string>? choices = null, string requester = Actor)
    {
        await Engine.RunAsync(runId, template, requester: requester);
        return await Engine.OpenWaitAsync(runId, kind, prompt: prompt, choices: choices);
    }

    /// <summary>
    /// Stops the API, does <paramref name="whileStopped"/> (such as appending records the console has not been told of),
    /// and starts it again on the same port with a new change feed, as a deploy or restart does.
    /// </summary>
    public async Task RestartAsync(Func<Task>? whileStopped = null)
    {
        await app.DisposeAsync();
        if (whileStopped is not null)
            await whileStopped();
        app = Start($"http://127.0.0.1:{Address.Port}");
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }
}
