using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using StepForm = FeatureFlow.Api.Contracts.StepForm;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The console (A4): the single-page application served as hashed assets with its policy, the text-only rule in its
/// sources, and the readable form it shows, from the libraries the run locked, with each step's status.
/// </summary>
public sealed class ConsoleTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

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
                new LibrarySourceConfig("std", "directory", Path.Combine(Repository.Root, "library", "std")),
                new LibrarySourceConfig("m7s", "directory", Path.Combine(Repository.Root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m7s")),
            ],
            new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"));
        app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!), "http://127.0.0.1:0");
        app.StartAsync(Ct).GetAwaiter().GetResult();
        return new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
    }

    [Fact]
    public async Task The_console_is_served_as_hashed_assets_with_its_policy()
    {
        using var http = Start();

        using var index = await http.GetAsync("console", Ct);
        using var slash = await http.GetAsync("console/", Ct);
        var html = await index.Content.ReadAsStringAsync(Ct);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (index.StatusCode, slash.StatusCode));
        Assert.Equal(html, await slash.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        Assert.True(index.Headers.CacheControl!.NoCache);
        AssertKeptToItsOrigin(index);
        // Every script and style the index loads is a hashed asset under /console/assets/, kept for a year.
        var assets = System.Text.RegularExpressions.Regex.Matches(html, @"<(script|link)\b[^>]*\b(src|href)=""([^""]+)""").Select(m => (m.Groups[1].Value, m.Groups[3].Value)).ToList();
        Assert.Contains(assets, a => a.Item1 == "script");
        Assert.Contains(assets, a => a.Item1 == "link");
        Assert.DoesNotContain("<style", html);
        foreach (var (tag, path) in assets)
        {
            Assert.Matches(@"^/console/assets/[\w.-]+-[\w-]{8}\.(js|css)$", path);
            using var asset = await http.GetAsync(path.TrimStart('/'), Ct);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal(tag == "script" ? "text/javascript" : "text/css", asset.Content.Headers.ContentType!.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl!.ToString());
            AssertKeptToItsOrigin(asset);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("console/assets/index-00000000.js", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("console/console.js", Ct)).StatusCode);
    }

    static void AssertKeptToItsOrigin(HttpResponseMessage response)
    {
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        foreach (var directive in (string[])["default-src 'self'", "object-src 'none'", "frame-ancestors 'none'", "base-uri 'none'", "form-action 'none'",
                     "require-trusted-types-for 'script'", "trusted-types 'none'"])
            Assert.Contains(directive, policy);
        Assert.DoesNotContain("unsafe-", policy);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    /// <summary>
    /// The text-only rule in the console's sources (LOG 455; the design's section 7): API data reaches the page only as text.
    /// No source uses a markup sink or evaluates text as code, and every link, image or form target comes from app/links.ts,
    /// because Preact does not refuse a <c>javascript:</c> URL. The bundle is not scanned: Preact itself holds a markup sink.
    /// </summary>
    [Fact]
    public void The_console_sources_never_build_markup_or_links_from_data()
    {
        var root = Path.Combine(Repository.Root, "src", "FeatureFlow.Console", "src");
        var sources = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".gen.d.ts", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(sources, f => Path.GetFileName(f) == "main.tsx");

        var sinks = new System.Text.RegularExpressions.Regex(
            @"dangerouslySetInnerHTML|\binnerHTML\b|\bouterHTML\b|\binsertAdjacentHTML\b|\bdocument\s*\.\s*write|\bDOMParser\b|\bcreateContextualFragment\b|\bsrcdoc\b|\beval\s*\(|\bnew\s+Function\b");
        // href, src or action given a value: a JSX attribute (no space before '='), a property, or through setAttribute.
        var links = new System.Text.RegularExpressions.Regex(
            @"((?<![\w.$])(href|src|action)=|\.(href|src|action)\s*=)(?!=)\s*(\{\s*)?(?<value>[^\s}>;]*)|setAttribute\(\s*[""'`](href|src|action)[""'`]");
        var found = new List<string>();
        foreach (var file in sources)
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            // Names this file imports from app/links.ts: the only calls a link may be built with.
            var fromLinks = System.Text.RegularExpressions.Regex.Matches(string.Join('\n', lines),
                    @"import\s+(\*\s+as\s+(?<ns>\w+)|\{(?<names>[^}]*)\})\s+from\s+[""'][./]*(app/)?links[""']")
                .SelectMany(m => m.Groups["ns"].Success ? [m.Groups["ns"].Value] : m.Groups["names"].Value.Split(',').Select(n => n.Split(" as ")[^1].Trim()))
                .Where(n => n.Length > 0).ToHashSet();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal))
                    continue;
                if (sinks.Match(line) is { Success: true } sink)
                    found.Add($"{name}:{i + 1}: {sink.Value}");
                if (name == "app/links.ts")
                    continue;
                foreach (System.Text.RegularExpressions.Match link in links.Matches(line))
                    if (!link.Groups["value"].Success || !fromLinks.Any(n => link.Groups["value"].Value.StartsWith(n + "(", StringComparison.Ordinal) ||
                                                                             link.Groups["value"].Value.StartsWith(n + ".", StringComparison.Ordinal)))
                        found.Add($"{name}:{i + 1}: {link.Value} (a link not built by app/links.ts)");
            }
        }

        Assert.Empty(found);
    }

    /// <summary>Starts a run through the API, which writes its lock and record before it reaches Temporal (absent here: a 500).</summary>
    async Task<RunRecord> StartRunAsync(HttpClient http, string template)
    {
        using (var started = await http.PostAsync("runs", new StringContent($$"""{ "template": "{{template}}" }""", Encoding.UTF8, "application/json"), Ct))
            Assert.Equal(HttpStatusCode.InternalServerError, started.StatusCode);
        return Assert.Single((await records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), Ct)).Unwrap());
    }

    /// <summary>Indexes plan revisions as the engine's record activity does: each stored as JSON, recorded under no step.</summary>
    async Task IndexAsync(IEnumerable<PlanSource> revisions)
    {
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        foreach (var revision in revisions)
        {
            var stored = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(revision.Write())), Ct)).Unwrap();
            await records.AppendAsync(new RecordBatch(Artifacts:
                [new ArtifactRecord(revision.RunId, "", revision.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, DateTimeOffset.UtcNow)]), Ct);
        }
    }

    static readonly LibrarySet Libraries = new(
        Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!,
        Library.Library.Open(Path.Combine(Repository.Root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m7s"), new DiagnosticBag())!);

    async Task<PlanView> PlanAsync(HttpClient http, string runId) =>
        JsonSerializer.Deserialize<PlanView>(await http.GetStringAsync($"runs/{runId}/plan", Ct), ApiJson.Options)!;

    [Fact]
    public async Task A_runs_plan_needs_the_token_and_shows_each_steps_status()
    {
        using var http = Start();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        var run = await StartRunAsync(http, "m7s:templates/decide");
        await IndexAsync(RunCore.Start(Libraries, LibraryReference.TryParse(run.Template)!, new RunInfo(run.RunId, [], new Dictionary<string, YNode>())).Core!.TakeRevisions());
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            Waits: [new WaitRecord(run.RunId, "gate-1", "gate", "go_ahead", "m7s.go_ahead", "Go ahead?", ["approved", "rejected"], true, now)],
            Timeline: [new TimelineRecord(run.RunId, 1, "started", "go_ahead", "execution 1", now)]), Ct);

        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"runs/{run.RunId}/plan", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("runs/run-nope/plan", Ct)).StatusCode);
        var plan = await PlanAsync(http, run.RunId);

        Assert.Equal((1, "m7s:templates/decide"), (plan.Revision, plan.Template));
        Assert.Contains("Template decide (level ticket)", plan.Readable);
        var row = Assert.Single(plan.Readable.Split('\n'), l => l.Contains("go_ahead", StringComparison.Ordinal));
        Assert.Contains("waiting(gate)", row);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(("go_ahead", StepForm.Decision, "m7s.go_ahead", "waiting(gate)", false), (step.Id, step.Form, step.What, step.Status, step.Breakpoint));
        Assert.Equal([("approved", "success", "end(completed)"), ("rejected", "failure", "end(failed)")], step.Outcomes.Select(o => (o.Name, o.Class, o.Route)));
    }

    [Fact]
    public async Task After_an_amend_the_plan_is_revision_2_with_the_new_step_and_both_revisions_are_listed()
    {
        using var http = Start();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        var run = await StartRunAsync(http, "m7s:templates/decide");
        var core = RunCore.Start(Libraries, LibraryReference.TryParse(run.Template)!, new RunInfo(run.RunId, [], new Dictionary<string, YNode>())).Core!;
        var amend = PatchReader.ReadWritten("""
            { api_version: feature-flow/v1, kind: patch, base_revision: 1, changes: [{ op: insert, before: go_ahead,
              step: { id: look_first, decision: { type: m7s.go_ahead, prompt: m7s:prompts/go_ahead }, outcomes: { approved: success, rejected: failure } } }] }
            """);
        Assert.Equal(PatchStatus.Applied, core.Apply(new PatchProposed(amend, new Proposer("human", null))).Patch!.Status);
        await IndexAsync(core.TakeRevisions());
        await records.AppendAsync(new RecordBatch(Timeline:
            [new TimelineRecord(run.RunId, 1, "breakpoint_set", "go_ahead", "by ann", DateTimeOffset.UtcNow)]), Ct);

        var plan = await PlanAsync(http, run.RunId);
        var listed = JsonSerializer.Deserialize<ArtifactList>(await http.GetStringAsync($"runs/{run.RunId}/artifacts", Ct), ApiJson.Options)!;

        Assert.Equal(2, plan.Revision);
        Assert.Equal(["look_first", "go_ahead"], plan.Steps.Select(s => s.Id));
        Assert.True(plan.Steps[1].Breakpoint);
        Assert.Equal([1, 2], listed.Artifacts.Where(a => a.Kind == ArtifactKind.PlanRevision).Select(a => a.Execution));
    }

    [Fact]
    public async Task After_an_epics_plan_expand_the_plan_shows_the_tickets()
    {
        using var http = Start();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        var run = await StartRunAsync(http, "std:templates/epic_delivery");
        var parameters = new Dictionary<string, YNode> { ["epic_name"] = YamlWriter.Read("\"billing\""), ["engine_can_merge"] = YamlWriter.Read("true") };
        var (core, d) = RunCore.Start(Libraries, LibraryReference.TryParse(run.Template)!, new RunInfo(run.RunId, ["acme/api"], parameters));
        Assert.True(core is not null, string.Join("\n", d));
        var items = (YSeq)YamlWriter.Read("""[{"name": "billing_api", "repositories": ["acme/api"]}, {"name": "billing_ui", "repositories": ["acme/api"]}, {"name": "billing_docs", "repositories": ["acme/api"]}]""");
        for (var i = 0; i < 100 && core.State.Revision == 1; i++)
            foreach (var command in core.Advance())
            {
                if (command is OpenGate gate)
                    core.Apply(new DecisionAnswered(gate.StepId, core.StepById(gate.StepId)!.DeclaredOutcomes.First().Name));
                else if (command is StartStep start && core.StepById(start.StepId) is { } step)
                {
                    if (step.Action == "plan.expand")
                    {
                        core.Apply(new StepCompleted(step.Id, "expanded", Patch: PlanExpansion.Build(core, step.Id, items.Items).Patch));
                        continue;
                    }

                    var outcome = step.Outcomes().First(o => o.Class == OutcomeClass.Success).Name;
                    core.Apply(new StepCompleted(step.Id, outcome, RunCore.ZeroOutputs(step),
                        step.Produces.Where(p => p.Outcome is null || p.Outcome == outcome).ToDictionary(p => p.Name, p => $"sha256:{step.Id}/{p.Name}")));
                }
            }

        await IndexAsync(core.TakeRevisions());
        var plan = await PlanAsync(http, run.RunId);

        Assert.Equal(2, plan.Revision);
        // F18's evidence: the plan a person reads now holds a child run per ticket of the work list.
        Assert.Equal(["tickets.children_0", "tickets.children_1", "tickets.children_2"],
            plan.Steps.Where(s => s is { Form: StepForm.Action, What: "run.child", Container: "tickets.children" }).Select(s => s.Id));
    }
}
