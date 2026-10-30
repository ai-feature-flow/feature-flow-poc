using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The named agent runtime instances, selected by role. docs/09 11.1: a role
/// names a capability, the organisation layer maps the role to an instance, and
/// nothing in the engine names a product.
///
/// <para>
/// The mapping below mirrors <c>organisation.yaml</c>'s <c>roleMapping</c>:
/// every role runs on <c>claude-code</c>.
/// </para>
/// </summary>
public sealed class RuntimeRegistry
{
    /// <summary>
    /// role -> runtime instance. From organisation.yaml roleMapping.
    ///
    /// <para>
    /// Review 14 A3 put every role on <c>copilot-cli</c>, because Claude Code
    /// could not reach the OpenRouter model. With a first-party Anthropic
    /// subscription on the machine, the product owner decided (2026-10-30) to
    /// prove <c>claude-code</c> on its own: every role, model <c>haiku</c>.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> RoleMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["planner"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["designer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["implementer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["fixer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
        ["reviewer"] = ClaudeCodeAgentRuntimeProvider.InstanceName,
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
        string workingDirectory,
        string recordDirectory,
        string runId)
    {
        var model = ResolveSecret(secrets, "model-default") ?? "z-ai/glm-5.3-flash";
        var endpoint = ResolveSecret(secrets, "model-router-endpoint")
                       ?? "https://openrouter.ai/api/v1";

        // Copilot CLI speaks the OpenAI-compatible shape and appends
        // /chat/completions itself, so its base URL carries /v1.
        var copilotEndpoint = Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT_COPILOT")
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

        // First-party subscription (2026-10-30): the CLI inherits the login
        // token from the worker's environment, so no key is brokered and no
        // router endpoint is set. Every session installs hooks that log each
        // event with its session id to the record store, so which events fired
        // is observed per step rather than asserted from a probe. User-level
        // settings, plugins and MCP servers are excluded so the session runs
        // only what the engine configured.
        var hookLog = Path.Combine(recordDirectory, $"{runId}.hook-events.jsonl");
        var hookSettings = ClaudeCodeCapabilityDetector.WriteHookSettings(
            hookLog,
            Path.GetFullPath(Path.Combine(workingDirectory, "..", "agent-runtime-work", $"{runId}.claude-settings.json")),
            allow: (Environment.GetEnvironmentVariable("FF_AGENT_ALLOW") is { Length: > 0 } extra
                    ? new[] { "Bash(git add:*)", "Bash(git commit:*)", "Bash(git status:*)", "Bash(git diff:*)" }
                        .Concat(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    : new[] { "Bash(git add:*)", "Bash(git commit:*)", "Bash(git status:*)", "Bash(git diff:*)" })
                .ToArray());

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
                HookSettingsPath: hookSettings,
                PermissionMode: "acceptEdits",
                ProbeOnCheck: true,
                ProbeTimeout: TimeSpan.FromSeconds(180),
                SubscriptionAuth: true,
                ExtraArguments: new[] { "--setting-sources", "project,local", "--strict-mcp-config" }));

        return new RuntimeRegistry(
            new Dictionary<string, IAgentRuntimeProvider>(StringComparer.OrdinalIgnoreCase)
            {
                [CopilotCliAgentRuntime.InstanceName] = copilot,
                [ClaudeCodeAgentRuntimeProvider.InstanceName] = claude,
            },
            ClaudeCodeAgentRuntimeProvider.InstanceName);
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

    /// <summary>A named instance, whether or not any role maps to it.</summary>
    public IAgentRuntimeProvider Instance(string name) => byInstance[name];

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
