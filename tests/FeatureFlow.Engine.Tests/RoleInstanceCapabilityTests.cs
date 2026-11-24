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
/// F16, check 21 (docs/09 section 2): a capability is asked of the instance a step uses. An agent step's runtime is the
/// one its role maps to (<c>role_mapping</c>), as the agent worker picks it, not the type's default; a dry run's is its
/// <c>dry_run</c> instance. Run start (<see cref="RunActivities.PrepareAsync"/>) on std's <c>ticket_delivery</c>, with two
/// agent-runtime instances: <c>full</c> offers every capability, <c>limited</c> has no MCP server.
/// </summary>
public sealed class RoleInstanceCapabilityTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-role-capability-" + Guid.NewGuid().ToString("N"));

    public RoleInstanceCapabilityTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    sealed class Runtime(string name, bool mcp) : IProvider
    {
        public ProviderDescription Describe() => new("agent_runtime", name, "0", new AgentRuntimeCapabilities(Hooks: true, Mcp: mcp));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    /// <summary>Run start with <paramref name="defaultRuntime"/> as the installation's runtime and the given role mapping and dry-run runtime.</summary>
    async Task<List<string>> Check21Async(string defaultRuntime, IReadOnlyDictionary<string, string> roleMapping, bool dryRun = false,
        string? dryRunRuntime = null, Func<string, string>? editManifest = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var artifacts = new FileSystemArtifactStore(Path.Combine(folder, Guid.NewGuid().ToString("N")));
        var std = Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), new DiagnosticBag())!;
        var files = std.Files.All.ToDictionary(kv => kv.Key, kv => kv.Key == "library.yaml" && editManifest is not null ? editManifest(kv.Value) : kv.Value);
        var library = Library.Library.FromFiles(new LibraryFiles(files), new DiagnosticBag())!;
        foreach (var (_, text) in library.Files.All)
            (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
        var lockText = JsonSerializer.Serialize(new LibraryLock([LibraryLock.Pin(library)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lockRef = (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;

        string[] others = ["vcs", "code_host", "ci", "sandbox", "tracker", "validator", "record_store"];
        var instances = others.ToDictionary(t => t, t => (IReadOnlyDictionary<string, IProvider>)new Dictionary<string, IProvider> { ["one"] = new DescribedOnlyProvider(t) });
        instances["artifact_store"] = new Dictionary<string, IProvider> { ["one"] = artifacts };
        instances["agent_runtime"] = new Dictionary<string, IProvider> { ["full"] = new Runtime("full", mcp: true), ["limited"] = new Runtime("limited", mcp: false) };
        var defaults = instances.Keys.ToDictionary(k => k, k => k == "agent_runtime" ? defaultRuntime : "one");
        var dryRunInstances = dryRun ? instances.Keys.ToDictionary(k => k, k => k == "agent_runtime" ? dryRunRuntime! : "one") : new Dictionary<string, string>();
        var providers = new ProviderSet(instances, defaults, dryRunInstances);

        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml")
        {
            RoleMapping = roleMapping.ToDictionary(kv => kv.Key, kv => new AgentRoleMapping(kv.Value, null)),
        };
        var activities = new RunActivities(new EngineServices(providers, installation, organisation), null);
        var start = new RunStart("run-1", "std:templates/ticket_delivery", ["acme/api"], new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" },
            lockRef, dryRun, "tester");
        var prepared = await new ActivityEnvironment().RunAsync(() => activities.PrepareAsync(start));
        return prepared.Errors.Where(e => e.Contains("check 21", StringComparison.Ordinal)).ToList();
    }

    static readonly Dictionary<string, string> None = [];

    [Fact]
    public async Task A_step_whose_role_maps_to_an_instance_lacking_a_capability_is_refused_at_run_start()
    {
        // Control: with no mapping every step uses the default instance, which offers the MCP server.
        Assert.Empty(await Check21Async("full", None));

        var refused = Assert.Single(await Check21Async("full", new Dictionary<string, string> { ["implementer"] = "limited" }));

        Assert.Contains("capability agent_runtime.mcp is not offered by agent_runtime instance 'limited' (role_mapping.implementer); needed by step implement",
            refused, StringComparison.Ordinal);
    }

    /// <summary>A role with no entry of its own runs on <c>role_mapping.default</c>'s instance, and the refusal names that entry (review R1-5).</summary>
    [Fact]
    public async Task A_refusal_names_the_default_entry_for_a_role_that_falls_to_it()
    {
        var refused = await Check21Async("full", new Dictionary<string, string> { ["default"] = "limited", ["implementer"] = "full" });

        var message = Assert.Single(refused);
        Assert.Contains("agent_runtime instance 'limited' (role_mapping.default)", message, StringComparison.Ordinal);
        Assert.Single(message.Split("role_mapping.").Skip(1));
    }

    [Fact]
    public async Task A_step_whose_role_maps_to_an_instance_offering_the_capability_starts_although_the_default_lacks_it()
    {
        // Control: the default instance lacks the MCP server, so every agent step is refused on it.
        Assert.Contains(await Check21Async("limited", None), e => e.Contains("capability agent_runtime.mcp is not offered by the configured agent_runtime provider", StringComparison.Ordinal));

        Assert.Empty(await Check21Async("limited", new Dictionary<string, string> { ["default"] = "full" }));
    }

    [Fact]
    public async Task A_dry_run_asks_its_dry_run_instance_whatever_the_role_mapping()
    {
        Assert.Empty(await Check21Async("full", new Dictionary<string, string> { ["default"] = "limited" }, dryRun: true, dryRunRuntime: "full"));

        var refused = await Check21Async("full", new Dictionary<string, string> { ["default"] = "full" }, dryRun: true, dryRunRuntime: "limited");
        Assert.Contains(refused, e => e.Contains("capability agent_runtime.mcp is not offered by the configured agent_runtime provider", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("code_host.review_comments")]
    [InlineData("tracker.custom_fields")]
    [InlineData("sandbox.docker")]
    public async Task A_library_requiring_a_removed_capability_is_refused_at_run_start_naming_it(string removed)
    {
        var refused = Assert.Single(await Check21Async("full", None,
            editManifest: text => text.Replace("capabilities: [code_host.pull_request]", $"capabilities: [code_host.pull_request, {removed}]", StringComparison.Ordinal)));

        Assert.Contains($"capability {removed} is not offered", refused, StringComparison.Ordinal);
        Assert.Contains("library std (requires.capabilities)", refused, StringComparison.Ordinal);
    }
}
