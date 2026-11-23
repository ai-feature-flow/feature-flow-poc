using System.Diagnostics;
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

namespace FeatureFlow.Api.Tests;

/// <summary>
/// An API over SQLite records, files and <see cref="FakeTemporal"/>, with the std library, for the console's reads (B1 A1, A2,
/// A8, A12): runs are started through the API, so they have a lock, and their plan revisions are indexed as the engine's record
/// activity indexes them. The caller is the local token's, with every role.
/// </summary>
public sealed class ReadsWorld : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-reads-" + Guid.NewGuid().ToString("N"));
    readonly Microsoft.AspNetCore.Builder.WebApplication app;

    public static readonly LibrarySet Libraries = new(Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!);

    public ReadsWorld()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        Records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        Artifacts = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = Records },
                ["artifact_store"] = new Dictionary<string, IProvider> { ["fs"] = Artifacts },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["artifact_store"] = "fs", ["secrets"] = "file" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "tester", ["viewer", "approver", "steerer", "admin"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var rules = new[] { "project.*", "epic.*", "ticket.*" }.Select(m => new ApprovalRule(m, ["approver"], 1, false, new Cap<bool>(false, false))).ToList();
        var organisation = new OrganisationConfig("t", [new LibrarySourceConfig("std", "directory", Path.Combine(Repository.Root, "library", "std"))],
            new PolicyConfig(rules, null, null, null, null), Path.Combine(folder, "organisation.yaml"));
        app = ApiApp.Build(new ApiServices(providers, installation, organisation, Temporal.Client), "http://127.0.0.1:0");
        app.StartAsync(Ct).GetAwaiter().GetResult();
        Http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
    }

    public SqliteRecordStore Records { get; }

    public FileSystemArtifactStore Artifacts { get; }

    public FakeTemporal Temporal { get; } = new();

    public HttpClient Http { get; }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    public async Task<T> GetAsync<T>(string path) => JsonSerializer.Deserialize<T>(await Http.GetStringAsync(path, Ct), ApiJson.Options)!;

    public async Task AppendAsync(RecordBatch batch) => (await Records.AppendAsync(batch, Ct)).Unwrap();

    /// <summary>Starts a root run through the API, which writes its record and lock, then records it as running with <paramref name="level"/>.</summary>
    public async Task<string> StartAsync(string template, string level, IReadOnlyDictionary<string, string> parameters, params string[] repositories)
    {
        var body = JsonSerializer.Serialize(new StartRunRequest(template, repositories, parameters), ApiJson.Options);
        using var started = await Http.PostAsync("runs", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var id = JsonSerializer.Deserialize<StartRunResponse>(await started.Content.ReadAsStringAsync(Ct), ApiJson.Options)!.RunId;
        await AppendAsync(new RecordBatch([Run(id, null, template, level, null, repositories)]));
        return id;
    }

    /// <summary>A running run's record, started at a time that orders runs by id.</summary>
    public static RunRecord Run(string id, string? parent, string template, string level, string? parentStep, IReadOnlyList<string> repositories, string status = "running") =>
        new(id, parent, template, status, "tester", repositories, false, DateTimeOffset.UtcNow, null, level, parentStep,
            StartedAt: new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero).AddSeconds(id.Length * 100 + id.Sum(c => c)));

    /// <summary>
    /// Indexes the run's plan revisions as the engine does: revision 1, and where <paramref name="children"/> is given, the run is
    /// driven to its <c>plan.expand</c> with them as the work list, which makes revision 2 with a <c>run.child</c> step for each.
    /// </summary>
    public async Task<Plan> IndexAsync(string runId, string template, IReadOnlyList<string> repositories, IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<YNode>? children = null, IReadOnlyList<string>? patches = null)
    {
        var (core, d) = RunCore.Start(Libraries, LibraryReference.TryParse(template)!,
            new RunInfo(runId, repositories, parameters.ToDictionary(p => p.Key, p => YamlWriter.Read(p.Value))));
        Assert.True(core is not null, string.Join("\n", d));
        foreach (var patch in patches ?? [])
        {
            var applied = core.Apply(new PatchProposed(PatchReader.ReadWritten(patch), new Proposer("human", null))).Patch!;
            Assert.True(applied.Status == PatchStatus.Applied, string.Join("; ", applied.Diagnostics));
        }

        for (var i = 0; children is not null && i < 200 && core.State.Revision == 1; i++)
            foreach (var command in core.Advance())
            {
                if (command is OpenGate gate)
                    core.Apply(new DecisionAnswered(gate.StepId, core.StepById(gate.StepId)!.DeclaredOutcomes.First().Name));
                else if (command is StartStep start && core.StepById(start.StepId) is { } step)
                {
                    if (step.Action == "plan.expand")
                    {
                        core.Apply(new StepCompleted(step.Id, "expanded", Patch: PlanExpansion.Build(core, step.Id, core.StepSpec(step.Id).Spec!.Inputs, children).Patch));
                        continue;
                    }

                    var outcome = step.Outcomes().First(o => o.Class == OutcomeClass.Success).Name;
                    core.Apply(new StepCompleted(step.Id, outcome, RunCore.ZeroOutputs(step),
                        step.Produces.Where(p => p.Outcome is null || p.Outcome == outcome).ToDictionary(p => p.Name, p => $"sha256:{step.Id}/{p.Name}")));
                }
            }

        foreach (var revision in core.TakeRevisions())
        {
            var stored = (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(revision.Write())), Ct)).Unwrap();
            await AppendAsync(new RecordBatch(Artifacts:
                [new ArtifactRecord(runId, "", revision.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, DateTimeOffset.UtcNow)]));
        }

        return core.Plan;
    }

    /// <summary>A work list of <paramref name="count"/> children, each named <c>&lt;prefix&gt;_&lt;n&gt;</c>, in <c>acme/api</c>.</summary>
    public static IReadOnlyList<YNode> WorkList(string prefix, int count) =>
        ((YSeq)YamlWriter.Read("[" + string.Join(", ", Enumerable.Range(0, count).Select(n => $$"""{"name": "{{prefix}}_{{n}}", "repositories": ["acme/api"]}""")) + "]")).Items;

    /// <summary>
    /// A project tree as the std templates make it: a project planned into <paramref name="epics"/> epics, each planned into
    /// <paramref name="tickets"/> tickets, every run with its plan and recorded as started by its parent's <c>run.child</c> step.
    /// </summary>
    public async Task<ProjectTree> ProjectAsync(int epics, int tickets)
    {
        string[] repositories = ["acme/api"];
        var projectParameters = new Dictionary<string, string> { ["project_name"] = "\"acme\"" };
        var project = await StartAsync("std:templates/project_delivery", "project", projectParameters, repositories);
        await IndexAsync(project, "std:templates/project_delivery", repositories, projectParameters, WorkList("epic", epics));
        var tree = new ProjectTree(project, [], []);
        for (var e = 0; e < epics; e++)
        {
            var epic = $"{project}.e{e}";
            var epicParameters = new Dictionary<string, string> { ["epic_name"] = $"\"epic_{e}\"", ["engine_can_merge"] = "true" };
            await AppendAsync(new RecordBatch([Run(epic, project, "std:templates/epic_delivery", "epic", $"epics.children_{e}", repositories)]));
            await IndexAsync(epic, "std:templates/epic_delivery", repositories, epicParameters, WorkList($"ticket_{e}", tickets));
            tree.Epics.Add(epic);
            for (var t = 0; t < tickets; t++)
            {
                var ticket = $"{epic}.t{t}";
                var ticketParameters = new Dictionary<string, string> { ["ticket_id"] = $"\"ticket_{e}_{t}\"", ["engine_can_merge"] = "true" };
                await AppendAsync(new RecordBatch([Run(ticket, epic, "std:templates/ticket_delivery", "ticket", $"tickets.children_{t}", repositories)]));
                await IndexAsync(ticket, "std:templates/ticket_delivery", repositories, ticketParameters);
                tree.Tickets.Add(ticket);
            }
        }

        return tree;
    }

    public sealed record ProjectTree(string Project, List<string> Epics, List<string> Tickets);
}

/// <summary>A run's tree in one read (B1 A12): from any run in it, the tree from its root, each run with its plan and open waits.</summary>
public sealed class RunTreeTests(ITestOutputHelper output) : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly ReadsWorld world = new();

    public ValueTask DisposeAsync() => world.DisposeAsync();

    static IEnumerable<RunTreeNode> Nodes(RunTreeNode node) => node.Children.SelectMany(Nodes).Prepend(node);

    [Fact]
    public async Task The_tree_read_from_a_ticket_is_its_projects_with_every_run_nested_under_its_parent()
    {
        var tree = await world.ProjectAsync(epics: 2, tickets: 2);
        var ticket = tree.Tickets[3];
        var now = DateTimeOffset.UtcNow;
        await world.AppendAsync(new RecordBatch(
            Waits:
            [
                new WaitRecord(ticket, "gate-1", "gate", "approve_spec", "ticket.spec_approval", "Approve?", ["approved", "rejected"], true, now),
                new WaitRecord(ticket, "gate-0", "gate", "approve_spec", "ticket.spec_approval", "Approve?", ["approved", "rejected"], false, now),
            ]));

        var read = await world.GetAsync<RunTreeView>($"runs/{ticket}/tree");

        Assert.Equal(tree.Project, read.Root.Run.RunId);
        Assert.Equal(tree.Epics, read.Root.Children.Select(c => c.Run.RunId));
        Assert.Equal(tree.Tickets, read.Root.Children.SelectMany(c => c.Children).Select(c => c.Run.RunId));
        Assert.All(Nodes(read.Root), n => Assert.Equal((n.Plan?.RunId, (string?)null), (n.Run.RunId, n.PlanNote)));
        // Each epic's plan names the child run its step started, as the child records it.
        var epic = read.Root.Children[1];
        Assert.Equal(epic.Children.Select(c => (c.Run.ParentStepId, (string?)c.Run.RunId)),
            epic.Plan!.Steps.Where(s => s.ChildRunId is not null).Select(s => ((string?)s.Id, s.ChildRunId)));
        var node = Nodes(read.Root).Single(n => n.Run.RunId == ticket);
        var wait = Assert.Single(node.Waits);
        Assert.Equal(("gate-1", true), (wait.WaitId, wait.CanAnswer));
        Assert.Equal("waiting(gate)", node.Plan!.Steps.Single(s => s.Id == "approve_spec").Status);
        Assert.Equal(await world.Http.GetStringAsync($"runs/{ticket}/tree", Ct), await world.Http.GetStringAsync($"runs/{tree.Project}/tree", Ct));
    }

    [Fact]
    public async Task A_run_with_no_plan_yet_says_so_and_the_tree_needs_a_viewer()
    {
        var root = await world.StartAsync("std:templates/ticket_delivery", "ticket", new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" });

        var read = await world.GetAsync<RunTreeView>($"runs/{root}/tree");
        using var anonymous = new HttpClient { BaseAddress = world.Http.BaseAddress };

        Assert.Null(read.Root.Plan);
        Assert.Equal($"run {root} has no plan yet", read.Root.PlanNote);
        Assert.Empty(read.Root.Children);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Http.GetAsync("runs/run-nope/tree", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"runs/{root}/tree", Ct)).StatusCode);
    }

    /// <summary>Plan risk 5: the cost of reading a 1 + 3 + 12 run tree, every run with its plan, once its lock's libraries are cached.</summary>
    [Fact]
    public async Task A_project_tree_of_sixteen_runs_reads_in_one_request()
    {
        var tree = await world.ProjectAsync(epics: 3, tickets: 4);

        var clock = Stopwatch.StartNew();
        var first = await world.GetAsync<RunTreeView>($"runs/{tree.Project}/tree");
        var cold = clock.Elapsed;
        var times = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            clock.Restart();
            await world.GetAsync<RunTreeView>($"runs/{tree.Tickets[i]}/tree");
            times.Add(clock.Elapsed);
        }

        var warm = times.Order().ElementAt(2);
        output.WriteLine($"1 + 3 + 12 run tree: first read {cold.TotalMilliseconds:F0} ms, warm median {warm.TotalMilliseconds:F0} ms " +
                         $"(of {string.Join(", ", times.Select(t => t.TotalMilliseconds.ToString("F0")))})");
        Assert.Equal(16, Nodes(first.Root).Count(n => n.Plan is not null));
        Assert.True(warm < TimeSpan.FromSeconds(5), $"a warm tree read took {warm}");
    }
}
