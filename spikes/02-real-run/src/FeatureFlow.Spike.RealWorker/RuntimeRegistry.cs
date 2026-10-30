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
    /// <summary>
    /// role -> runtime instance. From organisation.yaml roleMapping.
    ///
    /// <para>
    /// Review 14 A3: every role is on <c>copilot-cli</c>. <c>designer</c> and
    /// <c>implementer</c> were on <c>claude-code</c>, but Claude Code rejects
    /// the router's model with <c>[claude-code:unrecognized_model]</c> because it
    /// validates model names against Anthropic's own list and speaks the
    /// Anthropic Messages API rather than the OpenAI-compatible shape. The
    /// product owner decided to report Claude Code as not evidenced on A3 and
    /// proceed with Copilot CLI only.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> RoleMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["planner"] = CopilotCliAgentRuntime.InstanceName,
        ["designer"] = CopilotCliAgentRuntime.InstanceName,
        ["implementer"] = CopilotCliAgentRuntime.InstanceName,
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
    /// secrets store here, and from the organisation layer in production.
    ///
    /// <para>
    /// Review 14 A3. The model and the router endpoint are resolved from the
    /// secrets store <b>by reference</b> — <c>model-default</c> and
    /// <c>model-router-endpoint</c> — and the router's key is resolved from
    /// <c>openrouter-key-file</c>, which names a file outside the repository.
    /// No credential value is ever in configuration, in code, or in the
    /// repository (floor F2).
    /// </para>
    /// </summary>
    public static RuntimeRegistry Create(
        ISecretsProvider secrets,
        SecretScrubber scrubber,
        string workingDirectory)
    {
        var model = ResolveSecret(secrets, "model-default") ?? "z-ai/glm-5.3-flash";
        var endpoint = ResolveSecret(secrets, "model-router-endpoint")
                       ?? "https://openrouter.ai/api/v1";

        // Copilot CLI speaks the OpenAI-compatible shape and appends
        // /chat/completions itself, so its base URL carries /v1. Claude Code
        // speaks the Anthropic Messages API and appends /v1/messages, so its
        // base URL does not. Both are configuration, not code.
        var copilotEndpoint = Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT_COPILOT")
                              ?? endpoint;
        var claudeEndpoint = Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT_CLAUDE")
                             ?? endpoint;

        var workRoot = Path.Combine(workingDirectory, "..", "agent-runtime-work");

        var copilot = new CopilotCliAgentRuntime(
            new AgentRuntimeOptions(
                Executable: "copilot",
                Model: model,
                Endpoint: copilotEndpoint,
                EndpointType: "openai",
                EndpointKeyReference: new SecretReference("secrets", "openrouter-key-file"),
                WorkingRoot: workRoot),
            secrets: secrets,
            scrubber: scrubber);

        var claude = new ClaudeCodeAgentRuntimeProvider(
            new ModelGatewayCredentialBroker(
                secrets,
                scrubber: scrubber,
                keyRef: new SecretReference("secrets", "openrouter-key-file"),
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

    /// <summary>
    /// Reads one configuration value from the secrets store by reference.
    ///
    /// <para>
    /// A value that names an existing absolute file is that file's content, so
    /// a key reference can yield the key without the key ever being inlined —
    /// the same rule <c>LocalFileSecretsProvider</c> applies. A missing key is
    /// null rather than an exception, so the caller names its own fallback.
    /// </para>
    /// </summary>
    private static string? ResolveSecret(ISecretsProvider secrets, string key)
    {
        try
        {
            var resolved = secrets.ResolveAsync(
                new SecretReference("secrets", key), CancellationToken.None)
                .GetAwaiter().GetResult();

            return resolved.IsOk ? resolved.Value!.Value.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
