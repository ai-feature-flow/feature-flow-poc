using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// A dry run uses each type's <c>dry_run</c> instance and never falls back to the default one, except for the stores,
/// secrets and credential broker (docs/07 section 5, LOG 362): a type the plan needs with no dry-run instance is
/// absent, so check 21 refuses the run at start.
/// </summary>
public sealed class DryRunStartTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-dryrun-" + Guid.NewGuid().ToString("N"));

    public DryRunStartTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static readonly string[] Acting = ["vcs", "code_host", "ci", "sandbox", "tracker", "validator", "agent_runtime"];

    async Task<Prepared> PrepareAsync(bool dryRun, IEnumerable<string> dryRunTypes)
    {
        var ct = TestContext.Current.CancellationToken;
        var artifacts = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var std = Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!;
        foreach (var (_, text) in std.Files.All)
            (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
        var lockText = JsonSerializer.Serialize(new LibraryLock([LibraryLock.Pin(std)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lockRef = (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;

        var forge = new InMemoryForge();
        forge.CreateRepository("acme/api", new Dictionary<string, string> { ["README.md"] = "# api\n" });
        var made = new Dictionary<string, IProvider>
        {
            ["vcs"] = new InMemoryVcs(forge),
            ["code_host"] = new InMemoryCodeHost(forge),
            ["ci"] = new InMemoryCi(forge, () => artifacts),
            ["sandbox"] = new MemorySandbox(() => artifacts, forge),
            ["tracker"] = new InMemoryTracker(),
            ["validator"] = new InMemoryValidators(),
            ["agent_runtime"] = new ScriptedAgentRuntime(FakeScript.Empty, () => artifacts),
            ["artifact_store"] = artifacts,
            ["record_store"] = new InMemoryRecordStore(),
        };
        // One instance of each type, named "one", which is also the dry-run instance of the types given.
        var instances = made.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, IProvider>)new Dictionary<string, IProvider> { ["one"] = kv.Value });
        var providers = new ProviderSet(instances, made.Keys.ToDictionary(k => k, _ => "one"), dryRunTypes.ToDictionary(k => k, _ => "one"));
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        var activities = new RunActivities(new EngineServices(providers, installation, organisation), null);
        var start = new RunStart("run-1", "std:templates/ticket_delivery", ["acme/api"], new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" },
            lockRef, dryRun, "tester");
        return await new ActivityEnvironment().RunAsync(() => activities.PrepareAsync(start));
    }

    static List<string> Check21(Prepared prepared) => prepared.Errors.Where(e => e.Contains("check 21", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task A_dry_run_with_no_dry_run_instance_of_a_type_the_plan_needs_is_refused_at_start()
    {
        Assert.Empty(Check21(await PrepareAsync(dryRun: false, [])));
        Assert.Empty(Check21(await PrepareAsync(dryRun: true, Acting)));

        var refused = Check21(await PrepareAsync(dryRun: true, Acting.Where(t => t != "code_host")));
        Assert.NotEmpty(refused);
        Assert.All(refused, e => Assert.Contains("code_host", e, StringComparison.Ordinal));
    }
}
