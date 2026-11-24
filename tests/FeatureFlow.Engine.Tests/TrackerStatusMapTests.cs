using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Testing;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// L8, beside check 21 (docs/06 section 3.6): a status a tracker's <c>status_map</c> does not name is the tracker status of
/// the same name, so a map naming none of the statuses a plan moves issues to is refused at run start rather than run with
/// the library's names. Run start (<see cref="RunActivities.PrepareAsync"/>) on std's <c>ticket_delivery</c>, which moves
/// its issue to <c>in_progress</c>, <c>code_review</c> and <c>done</c>; the tracker's map is <see cref="InMemoryTracker"/>'s statuses.
/// </summary>
public sealed class TrackerStatusMapTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-status-map-" + Guid.NewGuid().ToString("N"));

    public TrackerStatusMapTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    async Task<List<string>> RefusalsAsync(ITrackerProvider tracker)
    {
        var ct = TestContext.Current.CancellationToken;
        var artifacts = new FileSystemArtifactStore(folder);
        var std = Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!;
        foreach (var (_, text) in std.Files.All)
            (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
        var lockText = JsonSerializer.Serialize(new LibraryLock([LibraryLock.Pin(std)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lockRef = (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;

        string[] others = ["vcs", "code_host", "ci", "sandbox", "agent_runtime", "validator", "record_store"];
        var instances = others.ToDictionary(t => t, t => (IReadOnlyDictionary<string, IProvider>)new Dictionary<string, IProvider> { ["one"] = new DescribedOnlyProvider(t) });
        instances["artifact_store"] = new Dictionary<string, IProvider> { ["one"] = artifacts };
        instances["tracker"] = new Dictionary<string, IProvider> { ["github"] = tracker };
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, k => k == "tracker" ? "github" : "one"), new Dictionary<string, string>());

        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        var activities = new RunActivities(new EngineServices(providers, installation, organisation), null);
        var start = new RunStart("run-1", "std:templates/ticket_delivery", ["acme/api"], new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" },
            lockRef, false, "tester");
        var prepared = await new ActivityEnvironment().RunAsync(() => activities.PrepareAsync(start));
        return prepared.Errors.Where(e => e.Contains("status_map", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public async Task A_tracker_whose_status_map_maps_none_of_the_plans_statuses_is_refused_at_run_start()
    {
        var refused = Assert.Single(await RefusalsAsync(new InMemoryTracker([])));

        Assert.Contains("check 21", refused, StringComparison.Ordinal);
        Assert.Contains("the status_map of tracker instance 'github' maps none of the statuses this plan moves issues to (code_review, done, in_progress)",
            refused, StringComparison.Ordinal);
        Assert.Single(await RefusalsAsync(new InMemoryTracker(["shipped"])));
    }

    [Fact]
    public async Task A_status_map_naming_one_of_the_plans_statuses_or_a_tracker_with_no_map_starts()
    {
        Assert.Empty(await RefusalsAsync(new InMemoryTracker(["done"])));
        Assert.Empty(await RefusalsAsync(new InMemoryTracker()));
    }
}
