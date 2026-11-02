namespace FeatureFlow.Providers.Abstractions;

// Typed capability records of docs/06 section 3. A run's needs are matched against them by name
// (ProviderDescription.CapabilityNames): CodeHostCapabilities.PullRequest is "code_host.pull_request".

public sealed record SandboxCapabilities(IReadOnlyList<string> Os, bool Docker, bool NetworkPolicy, bool PersistentAcrossSteps, bool StreamingOutput);

public sealed record AgentRuntimeCapabilities(bool Hooks, bool Mcp, bool SessionResume, bool StreamingToolCalls, bool UsageReporting,
    bool ModelConfiguration, bool CustomEndpoint, int? MaxContextTokens);

public sealed record VcsCapabilities(string System, bool HiddenReferences, bool Squash, bool PartialClone);

public sealed record CodeHostCapabilities(bool PullRequest, bool ReviewComments, bool CheckStatus, bool BranchProtection, bool MergeQueue, bool InboundEvents);

public sealed record CiCapabilities(bool Trigger, bool ReadLogs, bool Artifacts, bool InboundEvents, bool QueueBuilds);

public sealed record TrackerCapabilities(bool Issues, bool Hierarchy, bool Transitions, bool Links, bool Comments, bool CustomFields, bool Labels);

public sealed record FindingsSourceCapabilities(bool Fingerprints, bool Severity, bool Threads);

public sealed record NotificationCapabilities(IReadOnlyList<string> Channels, bool Interactive, bool Threads);

/// <summary>For provider types whose interface declares no capability flags: stores, secrets, validators and the rest.</summary>
public sealed record NoCapabilities;

public static class CapabilityRecords
{
    /// <summary>A record for <paramref name="type"/> with every flag set: what a fake provider of that type declares.</summary>
    public static object All(string type) => type switch
    {
        "sandbox" => new SandboxCapabilities(["linux"], true, true, true, true),
        "agent_runtime" => new AgentRuntimeCapabilities(true, true, true, true, true, true, true, null),
        "vcs" => new VcsCapabilities("git", true, true, true),
        "code_host" => new CodeHostCapabilities(true, true, true, true, true, true),
        "ci" => new CiCapabilities(true, true, true, true, true),
        "tracker" => new TrackerCapabilities(true, true, true, true, true, true, true),
        "findings_source" => new FindingsSourceCapabilities(true, true, true),
        "notifications" => new NotificationCapabilities(["local"], true, true),
        _ => new NoCapabilities(),
    };
}
