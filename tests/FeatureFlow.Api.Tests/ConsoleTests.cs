using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The minimal console (M7): a static page that holds no data and reads everything through the API with the person's
/// token, and the readable form it shows, from the libraries the run locked, with each step's status.
/// </summary>
public sealed class ConsoleTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Root { get; } = FindRoot();

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-console-" + Guid.NewGuid().ToString("N"));
    readonly SqliteRecordStore records;
    Microsoft.AspNetCore.Builder.WebApplication? app;

    public ConsoleTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
            await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above the test");
    }

    HttpClient Start()
    {
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["artifact_store"] = new Dictionary<string, IProvider> { ["fs"] = new FileSystemArtifactStore(Path.Combine(folder, "artifacts")) },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["artifact_store"] = "fs", ["secrets"] = "file" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "tester", ["viewer", "approver", "steerer", "admin"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t",
            [
                new LibrarySourceConfig("std", "directory", Path.Combine(Root, "library", "std")),
                new LibrarySourceConfig("m7s", "directory", Path.Combine(Root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m7s")),
            ],
            new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"));
        app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!), "http://127.0.0.1:0");
        app.StartAsync(Ct).GetAwaiter().GetResult();
        return new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
    }

    [Fact]
    public async Task The_page_and_its_script_are_served_with_no_data_and_kept_to_their_own_origin()
    {
        using var http = Start();

        using var page = await http.GetAsync("console", Ct);
        using var script = await http.GetAsync("console/console.js", Ct);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (page.StatusCode, script.StatusCode));
        Assert.Contains("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<script src=\"/console/console.js\"", await page.Content.ReadAsStringAsync(Ct));
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        // Run data reaches the page only as text: the script never parses it as markup.
        var js = await script.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("insertAdjacentHTML", js);
        Assert.Contains("\"/runs/\"", js.Replace('`', '"'));
    }

    [Fact]
    public async Task A_runs_readable_form_needs_the_token_and_shows_each_steps_status()
    {
        using var http = Start();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        // Starting a run writes its lock and record before it reaches Temporal, which this API does not have.
        using (var started = await http.PostAsync("runs", new StringContent("""{ "template": "m7s:templates/decide" }""", Encoding.UTF8, "application/json"), Ct))
            Assert.Equal(HttpStatusCode.InternalServerError, started.StatusCode);
        var run = Assert.Single((await records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), Ct)).Unwrap());
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            Waits: [new WaitRecord(run.RunId, "decision-1", "decision", "go_ahead", "m7s.go_ahead", "Go ahead?", ["approved", "rejected"], true, now)],
            Timeline: [new TimelineRecord(run.RunId, 1, "started", "go_ahead", "execution 1", now)]), Ct);

        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"runs/{run.RunId}/plan", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("runs/run-nope/plan", Ct)).StatusCode);
        var form = await http.GetStringAsync($"runs/{run.RunId}/plan", Ct);

        Assert.Contains("Template decide (level ticket)", form);
        var row = Assert.Single(form.Split('\n'), l => l.Contains("go_ahead", StringComparison.Ordinal));
        Assert.Contains("m7s.go_ahead", row);
        Assert.Contains("waiting(decision)", row);
    }
}
