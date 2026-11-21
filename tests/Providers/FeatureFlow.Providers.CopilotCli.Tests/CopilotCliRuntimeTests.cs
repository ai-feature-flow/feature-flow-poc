using System.Text.RegularExpressions;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli.Tests;

/// <summary>
/// What is Copilot CLI's own beyond the runtime contract (<see cref="CopilotCliRuntimeContract"/>): its custom provider and
/// offline mode (F6), only the role's tools with nothing loaded from the repository, its model, how it reports an
/// authentication failure, and its settings keys. The output shapes are those Copilot CLI 1.0.105 wrote in the sandbox (LOG 293).
/// </summary>
public sealed class CopilotCliRuntimeTests
{
    static readonly CopilotCliRuntime Runtime = new(new CopilotCliOptions("claude-haiku-4-5"),
        () => throw new InvalidOperationException("no sandbox in this test"), () => throw new InvalidOperationException("no store in this test"));

    static AgentTask Task(string writeAccess = "branch", IReadOnlyList<string>? tools = null, string? model = null) =>
        new(new StepIdentity("run-1", "t", "implement", 1, "run-1/implement/1"), "implementer", "Do it", new Dictionary<string, string>(),
            new StepContract([], new Dictionary<string, string>(), [], new Dictionary<string, string>()))
        {
            Tools = tools ?? ["read", "search", "edit", "shell"],
            WriteAccess = writeAccess,
            Engine = new EngineEndpoints(["http://host.docker.internal:8490"], "http://host.docker.internal:8490/model", "ffs1.step-token"),
            Paths = AgentPaths.Default,
            Questions = true,
            Model = model,
        };

    [Fact]
    public void The_runtime_s_environment_holds_the_step_token_and_no_model_or_github_credential()
    {
        var env = Runtime.Environment(Task(), new AgentLimits(null, 2));

        Assert.Equal("ffs1.step-token", env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("http://host.docker.internal:8490/model", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal(("anthropic", "true"), (env["COPILOT_PROVIDER_TYPE"], env["COPILOT_OFFLINE"]));
        Assert.Equal(("/ff/home", CopilotCliRuntime.Home("run-1/implement/1")), (env["HOME"], env["COPILOT_HOME"]));
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "COPILOT_API_URL", "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "COPILOT_PROVIDER_BEARER_TOKEN" })
            Assert.DoesNotContain(name, env.Keys);
    }

    /// <summary>
    /// LOG 497: with <c>auth: github</c> the CLI runs natively against the proxy, its GitHub token the step's, prefixed
    /// <c>ghs_</c>; not offline (which needs a custom provider) and no custom provider, so its own requests reach the proxy.
    /// </summary>
    [Fact]
    public void With_auth_github_the_step_token_is_the_cli_s_github_token_and_the_proxy_its_copilot_api()
    {
        var native = new CopilotCliRuntime(new CopilotCliOptions("claude-haiku-4-5", GitHub: true),
            () => throw new InvalidOperationException("no sandbox in this test"), () => throw new InvalidOperationException("no store in this test"));
        var env = native.Environment(Task(), new AgentLimits(null, 2));

        Assert.Equal("ghs_ffs1.step-token", env["COPILOT_GITHUB_TOKEN"]);
        Assert.Equal("http://host.docker.internal:8490/model", env["COPILOT_API_URL"]);
        Assert.Equal(("claude-haiku-4-5", "false", "true"), (env["COPILOT_MODEL"], env["COPILOT_AUTO_UPDATE"], env["COPILOT_TELEMETRY_DISABLED"]));
        Assert.Equal(CopilotCliRuntime.Home("run-1/implement/1"), env["COPILOT_HOME"]);
        Assert.DoesNotContain(env.Keys, k => k == "COPILOT_OFFLINE" || k.StartsWith("COPILOT_PROVIDER_", StringComparison.Ordinal));
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN" })
            Assert.DoesNotContain(name, env.Keys);
    }

    /// <summary>The factory reads <c>auth</c> from the instance's own <c>model_access</c>; any other is today's custom provider.</summary>
    [Theory]
    [InlineData("{ model_access: { mode: proxy, auth: github } }", true)]
    [InlineData("{ model_access: { mode: proxy, auth: api_key } }", false)]
    [InlineData("{ model_access: { mode: proxy, auth: oauth } }", false)]
    [InlineData("{ model_access: { mode: proxy } }", false)]
    public void The_factory_runs_the_cli_natively_only_for_auth_github(string yaml, bool github)
    {
        var settings = (YMap)YamlText.Parse(yaml, "installation.yaml", new DiagnosticBag())!;
        var runtime = (CopilotCliRuntime)CopilotCliProviders.Runtime().Create(new ProviderContext(
            new ProviderInstanceConfig("agent_runtime", "copilot", "copilot_cli", settings), ".", _ => null));

        Assert.Equal(github, runtime.Environment(Task(), new AgentLimits(null, 2)).ContainsKey("COPILOT_GITHUB_TOKEN"));
    }

    [Fact]
    public void The_model_is_the_role_s_else_the_instance_s()
    {
        Assert.Equal("claude-haiku-4-5", Runtime.Environment(Task(), new AgentLimits(null, 2))["COPILOT_MODEL"]);
        Assert.Equal("claude-opus-4-1", Runtime.Environment(Task(model: "claude-opus-4-1"), new AgentLimits(null, 2))["COPILOT_MODEL"]);
        Assert.Contains("'--model' 'claude-opus-4-1'", Runtime.CommandLine(Task(model: "claude-opus-4-1"), "/ff/agent/prompt-1.md", false), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_role_s_tools_are_available_and_nothing_is_loaded_from_the_repository()
    {
        var implementer = Runtime.CommandLine(Task(), "/ff/agent/prompt-1.md", false);
        var reviewer = Runtime.CommandLine(Task("none", ["read", "search", "shell"]), "/ff/agent/prompt-1.md", false);

        Assert.EndsWith("'--available-tools' 'view' 'grep' 'glob' 'create' 'edit' 'bash' 'engine' < '/ff/agent/prompt-1.md'", implementer, StringComparison.Ordinal);
        Assert.EndsWith("'--available-tools' 'view' 'grep' 'glob' 'bash' 'engine' < '/ff/agent/prompt-1.md'", reviewer, StringComparison.Ordinal);
        Assert.DoesNotContain("'write'", reviewer, StringComparison.Ordinal);
        foreach (var line in new[] { implementer, reviewer })
        {
            Assert.Contains("'--disable-builtin-mcps' '--no-custom-instructions'", line, StringComparison.Ordinal);
            // An untrusted folder: the repository's hooks, MCP servers and instructions are not loaded, and no permission is blanket.
            Assert.DoesNotContain("trust", line, StringComparison.Ordinal);
            Assert.DoesNotContain("'--allow-all-tools'", line, StringComparison.Ordinal);
            Assert.DoesNotContain("'--allow-all'", line, StringComparison.Ordinal);
            Assert.DoesNotContain("yolo", line, StringComparison.Ordinal);
        }
    }


    [Fact]
    public void A_model_that_cannot_be_reached_ends_resumably_and_a_refusal_is_a_configuration_fault()
    {
        const string unreachable = """
            {"type":"session.error","data":{"errorType":"query","message":"Failed to get response from the AI model; retried 5 times. Last error: tcp connect error"}}
            {"type":"result","exitCode":1}
            """;
        const string refused = """
            {"type":"session.error","data":{"errorType":"authentication","message":"Authentication failed with provider (HTTP 401)."}}
            {"type":"result","exitCode":1}
            """;
        var down = CopilotCliRuntime.Parse(unreachable, "");
        var no = CopilotCliRuntime.Parse(refused, "");

        Assert.Equal(SessionEnd.ModelUnreachable, SandboxedAgentRuntime.ApiEnd(down.ApiStatus));
        Assert.NotNull(down.ApiError);
        Assert.Equal(SessionEnd.ModelRefused, SandboxedAgentRuntime.ApiEnd(no.ApiStatus));
    }

    /// <summary>L5: no <c>engine_tools</c>.</summary>
    [Fact]
    public void The_runtime_takes_only_its_model_executable_and_model_access_as_settings() =>
        Assert.Equal(["model", "executable", "model_access"], Assert.Single(CopilotCliProviders.Factories).Settings);

    /// <summary>
    /// M8 follow-up 15, LOG 497: each step has its own <c>COPILOT_HOME</c>, root's and sticky, empty at its first session but
    /// for the engine's settings. A continuation keeps only what <c>--resume</c> reads (the session's state and
    /// <c>config.json</c>), so what an agent planted there (a skill, a <c>settings.local.json</c>) reaches no other step, no
    /// new session of its own and no continuation.
    /// </summary>
    [Fact]
    public async Task Each_step_has_its_own_root_sticky_copilot_home_and_a_continuation_keeps_only_the_session_s_state()
    {
        var sandbox = new HomeSandbox();
        var runtime = new CopilotCliRuntime(new CopilotCliOptions("claude-haiku-4-5"), () => sandbox, () => sandbox);
        AgentTask Step(string key, int session) => Task() with { Step = new StepIdentity("run-1", "t", "implement", 1, key), Session = session };
        var limits = new AgentLimits(null, 2);
        var ct = TestContext.Current.CancellationToken;

        await runtime.StartAsync(SandboxHandle.None, Step("run-1/implement/1", 1), limits, ct);
        await runtime.ContinueAsync(SandboxHandle.None, Step("run-1/implement/1", 2), limits, "Fix it.", ct);
        await runtime.StartAsync(SandboxHandle.None, Step("run-1/review/1", 1), limits, ct);
        await runtime.StartAsync(SandboxHandle.None, Step("run-1/implement/1", 3), limits, ct);

        var (first, continued, other, fresh) = (sandbox.Runs[0], sandbox.Runs[1], sandbox.Runs[2], sandbox.Runs[3]);
        Assert.Equal(CopilotCliRuntime.Home("run-1/implement/1"), first.Home);
        Assert.Equal(first.Home, continued.Home);
        Assert.NotEqual(first.Home, other.Home);
        Assert.StartsWith(CopilotCliRuntime.Homes + "/", other.Home, StringComparison.Ordinal);
        Assert.Equal([first.Home + "/mcp-config.json", first.Home + "/settings.json"], first.Found);
        Assert.Equal(new[] { "config.json", "mcp-config.json", "session-state/s/events.jsonl", "session-store.db", "settings.json" }.Select(f => first.Home + "/" + f),
            continued.Found);
        Assert.Equal([other.Home + "/mcp-config.json", other.Home + "/settings.json"], other.Found);
        Assert.Equal([fresh.Home + "/mcp-config.json", fresh.Home + "/settings.json"], fresh.Found);
        // The spike: a home with the wrong owner or mode makes the CLI exit silently; the sticky bit stops the agent replacing root's files.
        Assert.Equal(4, sandbox.Prepared.Count);
        foreach (var (run, (script, asRoot)) in sandbox.Runs.Zip(sandbox.Prepared))
        {
            Assert.True(asRoot);
            Assert.EndsWith($"chown root {run.Home} && chmod 1777 {run.Home}", script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A sandbox and store for <see cref="Each_step_has_its_own_root_sticky_copilot_home_and_a_continuation_keeps_only_the_session_s_state"/>:
    /// files by path; the runtime's <c>rm -rf</c> removes a folder, and its <c>find</c> removes the home's entries but those it
    /// names; the CLI records its home and the files there, then writes its state and plants a skill and a local settings file.
    /// </summary>
    sealed class HomeSandbox : ISandboxProvider, IArtifactStoreProvider
    {
        readonly SortedSet<string> files = new(StringComparer.Ordinal);

        public List<(string Home, List<string> Found)> Runs { get; } = [];

        public List<(string Script, bool AsRoot)> Prepared { get; } = [];

        public ProviderDescription Describe() => new("sandbox", "test", "0", new object());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken)
        {
            var script = command.Arguments[^1];
            if (script.StartsWith("exec ", StringComparison.Ordinal))
            {
                var home = command.Environment!["COPILOT_HOME"];
                Runs.Add((home, files.Where(f => f.StartsWith(home + "/", StringComparison.Ordinal)).ToList()));
                foreach (var made in new[] { "skills/planted.md", "settings.local.json", "session-state/s/events.jsonl", "session-store.db", "config.json" })
                    files.Add(home + "/" + made);
            }
            else if (key.EndsWith("/home", StringComparison.Ordinal))
            {
                Prepared.Add((script, command.AsRoot));
                foreach (Match m in Regex.Matches(script, @"rm -rf (/\S+)"))
                    files.RemoveWhere(f => f.StartsWith(m.Groups[1].Value + "/", StringComparison.Ordinal));
                if (Regex.Match(script, @"find (\S+) -mindepth 1 -maxdepth 1 (.*?) -exec rm -rf") is { Success: true } find)
                {
                    var kept = Regex.Matches(find.Groups[2].Value, @"! -name '([^']+)'")
                        .Select(n => new Regex("^" + Regex.Escape(n.Groups[1].Value).Replace(@"\*", ".*") + "$")).ToList();
                    files.RemoveWhere(f => f.StartsWith(find.Groups[1].Value + "/", StringComparison.Ordinal)
                                           && !kept.Any(k => k.IsMatch(f[(find.Groups[1].Value.Length + 1)..].Split('/')[0])));
                }
            }
            return Done(ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key)));
        }

        public Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(CommandHandle command, long cursor, CancellationToken cancellationToken) =>
            Done(ProviderResult<CommandStatus>.Success(new CommandStatus(false, 0, "", 0)));

        public Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken) =>
            Done(ProviderResult<CommandHandle>.Failure("not_found", key));

        public Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle sandbox, ArtifactRef source, string path, bool readOnly, CancellationToken cancellationToken)
        {
            files.Add(path);
            return Done(ProviderResult<Unit>.Success(default));
        }

        public Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken cancellationToken) => Done(ProviderResult<ArtifactRef>.Success(new ArtifactRef("x")));

        public Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<Unit>> MakeReadOnlyAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken cancellationToken) => throw new NotSupportedException();

        static Task<T> Done<T>(T value) => System.Threading.Tasks.Task.FromResult(value);
    }
}
