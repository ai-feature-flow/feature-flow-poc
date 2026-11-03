using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.ClaudeCode.Tests;

/// <summary>
/// The Claude Code runtime's settings (docs/06 section 3.2): DR16's timeouts, an environment with no model
/// credential (F6) and the engine's home (DR19), and settings that only the engine supplies.
/// </summary>
public sealed class ClaudeCodeRuntimeTests
{
    static readonly ClaudeCodeRuntime Runtime = new(new ClaudeCodeOptions("haiku"),
        () => throw new InvalidOperationException("no sandbox in this test"), () => throw new InvalidOperationException("no store in this test"));

    static AgentTask Task(string writeAccess = "branch", IReadOnlyList<string>? tools = null) =>
        new(new StepIdentity("run-1", "t", "implement", 1, "run-1/implement/1"), "implementer", "Do it", new Dictionary<string, string>(),
            new StepContract([], new Dictionary<string, string>(), [], new Dictionary<string, string>()))
        {
            Tools = tools ?? ["read", "search", "edit", "shell"],
            WriteAccess = writeAccess,
            Engine = new EngineEndpoints(["http://host.docker.internal:8490"], "http://host.docker.internal:8490/model", "ffs1.step-token"),
            Paths = AgentPaths.Default,
            Questions = true,
        };

    /// <summary>
    /// DR16: the MCP tool timeout exceeds the question timeout; the hook command's timeout exceeds the client's hold.
    /// The hold is the fixed default (LOG 91), and a question with no timeout set waits 24 hours (LOG 106).
    /// </summary>
    [Theory]
    [InlineData(4.0)]
    [InlineData(24.0)]
    [InlineData(null)]
    public void The_runtime_s_tool_and_hook_timeouts_exceed_the_waits_they_carry(double? questionHours)
    {
        var limits = new AgentLimits(null, 2, questionHours is { } h ? TimeSpan.FromHours(h) : null);
        var env = Runtime.Environment(Task(), limits);
        Assert.True(long.Parse(env["MCP_TOOL_TIMEOUT"], System.Globalization.CultureInfo.InvariantCulture) > TimeSpan.FromHours(questionHours ?? 24).TotalMilliseconds);
        var hookTimeout = Runtime.Settings()["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>();
        Assert.True(hookTimeout > int.Parse(env["FF_HOOK_HOLD_SECONDS"], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>LOG 91: Claude Code's retries, the hook's hold and the client's retry are fixed defaults, not settings.</summary>
    [Fact]
    public void The_runtime_takes_only_its_model_executable_model_access_and_engine_tools_as_settings()
    {
        Assert.Equal(["model", "executable", "model_access", "engine_tools"], Assert.Single(ClaudeCodeProviders.Factories).Settings);
        var defaults = new ClaudeCodeOptions("haiku");
        Assert.Equal((null, TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(10)), (defaults.MaxRetries, defaults.Hold, defaults.Retry));
        Assert.DoesNotContain("CLAUDE_CODE_MAX_RETRIES", Runtime.Environment(Task(), new AgentLimits(null, 2)).Keys);
    }

    /// <summary>F6 and DR19: the runtime gets the step's token and the proxy's URL, a home the engine controls, and no model credential.</summary>
    [Fact]
    public void The_runtime_s_environment_holds_the_step_token_and_no_model_credential()
    {
        var env = Runtime.Environment(Task(), new AgentLimits(null, 2));
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", env.Keys);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", env.Keys);
        Assert.Equal("ffs1.step-token", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("http://host.docker.internal:8490/model", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal(AgentPaths.Default.Home, env["HOME"]);
        Assert.StartsWith(AgentPaths.Default.Home, env["CLAUDE_CONFIG_DIR"], StringComparison.Ordinal);
    }

    /// <summary>A repository's own settings, hooks or MCP servers are not loaded: only the engine's.</summary>
    [Fact]
    public void The_command_line_loads_only_the_engine_s_settings_and_mcp_server()
    {
        var line = Runtime.CommandLine(Task(), "/ff/agent/prompt-1.md", resume: false);
        Assert.Contains("'--setting-sources' 'user'", line);
        Assert.Contains("'--strict-mcp-config' '--mcp-config' '/ff/agent/mcp.json'", line);
        Assert.Contains("'--session-id' '" + ClaudeCodeRuntime.SessionId("run-1/implement/1") + "'", line);
        Assert.Contains("--resume", Runtime.CommandLine(Task(), "/ff/agent/prompt-2.md", resume: true));
    }

    [Fact]
    public void A_role_with_no_write_access_is_given_no_editing_tool()
    {
        var tools = Runtime.Tools(Task("none", ["read", "search", "edit", "shell"]));
        Assert.DoesNotContain("Write", tools);
        Assert.DoesNotContain("Edit", tools);
        Assert.Contains("Bash", tools);
        Assert.DoesNotContain("mcp__engine__checkpoint", Runtime.CommandLine(Task("none"), "p", false));
    }

    /// <summary>Review finding 7: hooks come from the root-owned managed settings, and no other source's hooks run.</summary>
    [Fact]
    public void Hooks_are_managed_settings_that_allow_no_other_hooks()
    {
        Assert.True(Runtime.Settings()["allowManagedHooksOnly"]!.GetValue<bool>());
        Assert.StartsWith("/etc/claude-code/", ClaudeCodeRuntime.ManagedSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("--settings", Runtime.CommandLine(Task(), "p", false));
    }

    /// <summary>Review finding 5: only transient API errors are resumable.</summary>
    [Theory]
    [InlineData(null, SessionEnd.ModelUnreachable)]
    [InlineData(429, SessionEnd.ModelUnreachable)]
    [InlineData(500, SessionEnd.ModelUnreachable)]
    [InlineData(529, SessionEnd.ModelUnreachable)]
    [InlineData(401, SessionEnd.ModelRefused)]
    [InlineData(403, SessionEnd.ModelRefused)]
    [InlineData(400, SessionEnd.Failed)]
    [InlineData(413, SessionEnd.Failed)]
    public void Only_transient_api_errors_are_resumable(int? status, string end)
    {
        Assert.Equal(end, ClaudeCodeRuntime.ApiEnd(status));
        var parsed = ClaudeCodeRuntime.Parse($$"""{"type":"result","terminal_reason":"api_error","api_error":"x","api_error_status":{{(status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}}}""");
        Assert.Equal(status, parsed.ApiStatus);
    }

    [Fact]
    public void The_session_id_is_fixed_by_the_step_s_key() =>
        Assert.Equal(ClaudeCodeRuntime.SessionId("a/b/1"), ClaudeCodeRuntime.SessionId("a/b/1"));

    /// <summary>A session that ended on an API error is the model unreachable, not a work failure (SP5 N1).</summary>
    [Fact]
    public void An_api_error_in_the_result_record_is_read_as_the_model_unreachable()
    {
        var transcript = ClaudeCodeRuntime.Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/ff/work/a.txt"}}]}}
            {"type":"result","terminal_reason":"api_error","api_error":"connection_failed","result":"API Error","usage":{"input_tokens":5,"output_tokens":7}}
            """);
        Assert.Equal("connection_failed", transcript.ApiError);
        Assert.Equal(["/ff/work/a.txt"], transcript.Files);
        Assert.Equal((5L, 7L), (transcript.InputTokens, transcript.OutputTokens));
        Assert.Null(ClaudeCodeRuntime.Parse("""{"type":"result","terminal_reason":"completed","result":"ok"}""").ApiError);
    }
}
