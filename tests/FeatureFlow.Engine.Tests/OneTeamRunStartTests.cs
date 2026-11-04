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
/// Run start refuses a run, a child included, whose repositories belong to more than one team (LOG 217), with its own
/// message; the API refuses such a start too, but a child is started by its parent, not through the API.
/// </summary>
public sealed class OneTeamRunStartTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-oneteam-" + Guid.NewGuid().ToString("N"));

    public OneTeamRunStartTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static string StdRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return Path.Combine(dir.FullName, "library", "std");
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    async Task<Prepared> PrepareAsync(params string[] repositories)
    {
        var ct = TestContext.Current.CancellationToken;
        var artifacts = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var std = Library.Library.Open(StdRoot(), new DiagnosticBag())!;
        foreach (var (_, text) in std.Files.All)
            (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
        var lockText = JsonSerializer.Serialize(new LibraryLock([LibraryLock.Pin(std)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lockRef = (await artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;

        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["artifact_store"] = new Dictionary<string, IProvider> { ["one"] = artifacts },
            ["record_store"] = new Dictionary<string, IProvider> { ["one"] = new InMemoryRecordStore() },
        };
        var providers = new ProviderSet(instances, new Dictionary<string, string> { ["artifact_store"] = "one", ["record_store"] = "one" }, new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml")
        {
            Teams = new Dictionary<string, TeamConfig>
            {
                ["misc"] = new("misc", ["acme/misc"], [], new Dictionary<string, PeopleRoleConfig>()),
                ["pay"] = new("pay", ["acme/pay"], [], new Dictionary<string, PeopleRoleConfig>()),
            },
        };
        var activities = new RunActivities(new EngineServices(providers, installation, organisation), null);
        var start = new RunStart("child-1", "std:templates/ticket_delivery", repositories, new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" },
            lockRef, false, "tester", ParentWorkflowId: "root");
        return await new ActivityEnvironment().RunAsync(() => activities.PrepareAsync(start));
    }

    [Fact]
    public async Task A_child_whose_repositories_span_two_teams_is_refused_at_run_start()
    {
        var spanning = await PrepareAsync("acme/misc", "acme/pay");
        var one = await PrepareAsync("acme/pay");

        Assert.Contains("the run's repositories (acme/misc, acme/pay) belong to more than one team (misc, pay); start one run per team", spanning.Errors);
        Assert.DoesNotContain(one.Errors, e => e.Contains("more than one team", StringComparison.Ordinal));
    }
}
