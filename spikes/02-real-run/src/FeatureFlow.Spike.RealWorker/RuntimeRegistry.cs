using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The named agent runtime instances, selected by role. docs/09 11.1: a role
/// names a capability, the organisation layer maps the role to an instance, and
/// nothing in the engine names a product.
///
/// <para>
/// The mapping below is <c>organisation.yaml</c>'s <c>roleMapping</c>, read as
/// data rather than hardcoded in a step: planner and fixer run on
/// <c>copilot-cli</c>, designer and implementer on <c>claude-code</c>.
/// </para>
/// </summary>
public sealed class RuntimeRegistry
{
    /// <summary>role -> runtime instance. From organisation.yaml roleMapping.</summary>
    private static readonly Dictionary<string, string> RoleMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["planner"] = CopilotCliAgentRuntime.InstanceName,
        ["designer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["implementer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["fixer"] = CopilotCliAgentRuntime.InstanceName,
        ["reviewer"] = CopilotCliAgentRuntime.InstanceName,
    };

    private readonly Dictionary<string, IAgentRuntimeProvider> byInstance =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string defaultInstance;

    private RuntimeRegistry(
        Dictionary<string, IAgentRuntimeProvider> instances,
        string defaultInstance)
    {
        byInstance = instances;
        this.defaultInstance = defaultInstance;
    }

    /// <summary>
    /// Builds both instances. Model and endpoint are configuration, never
    /// template content (docs/09 11.1, brief lesson 5): they come from the
    /// environment here, and from the organisation layer in production.
    /// </summary>
    public static RuntimeRegistry Create(
        ISecretsProvider secrets,
        SecretScrubber scrubber,
        string workingDirectory)
    {
        var model = Environment.GetEnvironmentVariable("FF_MODEL") ?? "qwen2.5-coder:7b";

        // Copilot CLI speaks the OpenAI-compatible shape and appends
        // /chat/completions itself, so its base URL carries /v1. Claude Code
        // speaks the Anthropic Messages API and appends /v1/messages, so its
        // base URL does not. Both are configuration, not code.
        var copilotEndpoint = Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT_COPILOT")
                              ?? "http://localhost:11434/v1";
        var claudeEndpoint = Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT_CLAUDE")
                             ?? "http://localhost:11434";

        var workRoot = Path.Combine(workingDirectory, "..", "agent-runtime-work");

        var copilot = new CopilotCliAgentRuntime(
            new AgentRuntimeOptions(
                Executable: "copilot",
                Model: model,
                Endpoint: copilotEndpoint,
                EndpointType: "openai",
                EndpointKeyReference: new SecretReference("secrets", "model-api-key"),
                WorkingRoot: workRoot),
            secrets: secrets,
            scrubber: scrubber);

        var claude = new ClaudeCodeAgentRuntimeProvider(
            new ModelGatewayCredentialBroker(
                secrets,
                scrubber: scrubber,
                keyRef: new SecretReference("secrets", "model-api-key"),
                grantable: new[]
                {
                    GrantableScope.ForRepository(
                        $"{RunContext.Owner}/{RunContext.Repository}", "model:write"),
                }),
            new ClaudeCodeRuntimeOptions(
                Model: model,
                Endpoint: claudeEndpoint,
                PermissionMode: "acceptEdits",
                ProbeOnCheck: true,
                ProbeTimeout: TimeSpan.FromSeconds(180)));

        return new RuntimeRegistry(
            new Dictionary<string, IAgentRuntimeProvider>(StringComparer.OrdinalIgnoreCase)
            {
                [CopilotCliAgentRuntime.InstanceName] = copilot,
                [ClaudeCodeAgentRuntimeProvider.InstanceName] = claude,
            },
            CopilotCliAgentRuntime.InstanceName);
    }

    /// <summary>The instance a role runs on. Unknown roles get the default.</summary>
    public IAgentRuntimeProvider ForRole(string? role)
    {
        if (!string.IsNullOrWhiteSpace(role) &&
            RoleMapping.TryGetValue(RoleName(role), out var instance) &&
            byInstance.TryGetValue(instance, out var mapped))
        {
            return mapped;
        }

        return byInstance[defaultInstance];
    }

    /// <summary>The instance name a role maps to, for the record.</summary>
    public string InstanceForRole(string? role) =>
        !string.IsNullOrWhiteSpace(role) && RoleMapping.TryGetValue(RoleName(role), out var instance)
            ? instance
            : defaultInstance;

    public IReadOnlyDictionary<string, string> Mapping => RoleMapping;

    /// <summary>
    /// "feature-flow-default:roles/planner" -> "planner". A role reference is
    /// content; the last segment is the role name the organisation maps.
    /// </summary>
    private static string RoleName(string role)
    {
        var slash = role.LastIndexOf('/');
        return slash >= 0 ? role[(slash + 1)..] : role;
    }
}
