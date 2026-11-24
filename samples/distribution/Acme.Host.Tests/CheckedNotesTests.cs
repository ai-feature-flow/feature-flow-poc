using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acme.Host;
using Acme.Providers;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Testing;

namespace Acme.Host.Tests;

/// <summary>
/// Acme's host, in this process, runs the sample library's <c>checked_notes</c> against the Temporal dev server named by
/// <c>FF_TEMPORAL_ADDRESS</c>, with a development installation: notes are imported from an in-memory repository and checked
/// by Acme's own validator, <c>acme_line_limit</c>, which the engine does not ship.
/// </summary>
public sealed class CheckedNotesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_checks_its_notes_with_the_acme_validator()
    {
        var address = Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") is { Length: > 0 } a
            ? a : throw new InvalidOperationException("set FF_TEMPORAL_ADDRESS to a Temporal dev server, such as localhost:7233");
        var folder = Directory.CreateTempSubdirectory("acme-host-").FullName;
        var world = "acme-" + Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(folder, "secrets.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["api_token"] = token }), Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "{{address}}", namespace: default, task_queue: acme-{{Guid.NewGuid():N}} }
            providers:
              artifact_store: { default: filesystem, instances: { filesystem: { root: artifacts } } }
              record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
              secrets: { default: file, instances: { file: { path: secrets.json } } }
              code_host: { default: memory, instances: { memory: { world: {{world}} } } }
              validator: { default: acme, instances: { acme: {} } }
            api:
              auth: { mode: local_token, token_ref: secrets/api_token, actor: tester, roles: [viewer, approver, steerer, admin] }
            """, Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries:
              - { name: acme, source: directory, path: "{{Library()}}" }
            policy:
              approval:
                - { match: "*", roles: [approver], auto: false, from_chat: false }
            """, Ct);

        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository("acme/notes");
        var shortNotes = forge.Commit(repo, "main", new Dictionary<string, string?> { ["notes.md"] = "# Notes\n\nShort.\n" });
        var longNotes = forge.Commit(repo, "main", new Dictionary<string, string?> { ["notes.md"] = "# Notes\n\nOne.\nTwo.\nThree.\nFour.\n" });

        var configuration = FeatureFlowConfiguration.Load(Path.Combine(folder, "installation.yaml"), Path.Combine(folder, "organisation.yaml"));
        await using var host = await FeatureFlowHost.StartAsync(configuration, AcmeHost.Factories, orchestrationWorker: true, agentWorker: false, apiUrl: "http://127.0.0.1:0");
        using var http = new HttpClient { BaseAddress = new Uri(host.ApiAddress!) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var before = AcmeValidators.Calls;

        var within = await RunAsync(http, shortNotes);
        var over = await RunAsync(http, longNotes);

        Assert.Equal("completed", within);
        Assert.Equal("rejected", over);
        Assert.Equal(before + 2, AcmeValidators.Calls);
    }

    /// <summary>Starts <c>acme:templates/checked_notes</c> on notes.md at <paramref name="commit"/> through the API and waits for its end.</summary>
    static async Task<string> RunAsync(HttpClient http, string commit)
    {
        var start = await http.PostAsJsonAsync("runs", new
        {
            template = "acme:templates/checked_notes",
            parameters = new Dictionary<string, string>
            {
                ["repository"] = "\"acme/notes\"",
                ["commit"] = JsonSerializer.Serialize(commit),
                ["max_lines"] = "3",
            },
        }, Ct);
        var started = await start.Content.ReadAsStringAsync(Ct);
        Assert.True(start.IsSuccessStatusCode, started);
        var runId = JsonNode.Parse(started)!["run_id"]!.ToString();

        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            var status = JsonNode.Parse(await http.GetStringAsync($"runs/{runId}", Ct))!["run"]!["status"]!.ToString();
            if (status is not ("starting" or "running"))
                return status;
            await Task.Delay(200, Ct);
        }

        throw new TimeoutException($"{runId} did not end within a minute");
    }

    /// <summary>The sample's library folder, found above the test's output folder.</summary>
    static string Library()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "library", "library.yaml")))
                return Path.Combine(dir.FullName, "library");
        throw new DirectoryNotFoundException("no library/library.yaml above " + AppContext.BaseDirectory);
    }
}
