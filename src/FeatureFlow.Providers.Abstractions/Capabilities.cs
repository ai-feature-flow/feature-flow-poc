namespace FeatureFlow.Providers.Abstractions;

// Typed capability records of docs/06 section 3. A run's needs are matched against them by name
// (ProviderDescription.CapabilityNames): CodeHostCapabilities.PullRequest is "code_host.pull_request". A record holds
// only the flags the engine reads (check 21); a type whose interface declares none describes NoCapabilities.

/// <summary>What every agent step needs of the runtime its role maps to (<c>RunStartChecks.AgentNeeds</c>): the hook reading and the MCP server.</summary>
public sealed record AgentRuntimeCapabilities(bool Hooks, bool Mcp);

/// <summary>Read from the action descriptors: <c>pull_request.*</c> and the status-check waits.</summary>
public sealed record CodeHostCapabilities(bool PullRequest, bool CheckStatus);

/// <summary>For provider types whose interface declares no capability flags: sandboxes, stores, trackers and the rest.</summary>
public sealed record NoCapabilities;

public static class CapabilityRecords
{
    /// <summary>A record for <paramref name="type"/> with every flag set: what a fake provider of that type declares.</summary>
    public static object All(string type) => type switch
    {
        "agent_runtime" => new AgentRuntimeCapabilities(true, true),
        "code_host" => new CodeHostCapabilities(true, true),
        _ => new NoCapabilities(),
    };
}
