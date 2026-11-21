namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>Which instance a run uses for a type (docs/07 sections 2 and 5): the one it chose, the default, or a dry run's <c>dry_run</c> instance.</summary>
public sealed class ProviderSetTests
{
    sealed class Stub : IProvider
    {
        public ProviderDescription Describe() => new("agent_runtime", "stub", "0", new object());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    static readonly ProviderSet Providers = new(
        new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["agent_runtime"] = new Dictionary<string, IProvider> { ["claude"] = new Stub(), ["copilot"] = new Stub(), ["scripted"] = new Stub() },
            ["record_store"] = new Dictionary<string, IProvider> { ["postgres"] = new Stub() },
            ["tracker"] = new Dictionary<string, IProvider> { ["jira"] = new Stub() },
        },
        new Dictionary<string, string> { ["agent_runtime"] = "claude", ["record_store"] = "postgres", ["tracker"] = "jira" },
        new Dictionary<string, string> { ["agent_runtime"] = "scripted" });

    /// <summary>The name is the instance <see cref="ProviderSet.ForRun(string, string, bool)"/> returns, so the agent step's token names the instance it runs on.</summary>
    [Theory]
    [InlineData("agent_runtime", "copilot", false, "copilot")]
    [InlineData("agent_runtime", null, false, "claude")]
    [InlineData("agent_runtime", "copilot", true, "scripted")]
    [InlineData("record_store", null, true, "postgres")]
    [InlineData("tracker", null, true, null)]
    public void The_instance_a_run_uses_is_named_as_it_is_chosen(string type, string? chosen, bool dryRun, string? name)
    {
        Assert.Equal(name, Providers.NameForRun(type, chosen, dryRun));
        Assert.Same(name is null ? null : Providers.Instance(type, name),
            chosen is null ? Providers.ForRun(type, dryRun) : Providers.ForRun(type, chosen, dryRun));
    }
}
