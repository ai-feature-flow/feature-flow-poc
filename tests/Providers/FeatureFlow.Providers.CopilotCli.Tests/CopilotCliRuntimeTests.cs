using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli.Tests;

/// <summary>
/// The Copilot CLI runtime's settings (docs/06 section 3.2): DR16's timeouts, an environment with no model or GitHub
/// credential (F6) and the engine's home (DR19), only the role's tools, nothing loaded from the repository, and how its
/// output is read. The output shapes are those Copilot CLI 1.0.105 wrote in the sandbox (LOG 293).
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

    [Theory]
    [InlineData(4.0)]
    [InlineData(24.0)]
    [InlineData(null)]
    public void The_runtime_s_tool_and_hook_timeouts_exceed_the_waits_they_carry(double? questionHours)
    {
        var limits = new AgentLimits(null, 2, questionHours is { } h ? TimeSpan.FromHours(h) : null);
        var mcp = Runtime.McpConfig(Task(), limits)["mcpServers"]!["engine"]!["timeout"]!.GetValue<long>();
        Assert.True(mcp > TimeSpan.FromHours(questionHours ?? 24).TotalMilliseconds);
        var hookTimeout = Runtime.Settings()["hooks"]!["PreToolUse"]![0]!["timeoutSec"]!.GetValue<int>();
        Assert.True(hookTimeout > int.Parse(Runtime.Environment(Task(), limits)["FF_HOOK_HOLD_SECONDS"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_runtime_s_environment_holds_the_step_token_and_no_model_or_github_credential()
    {
        var env = Runtime.Environment(Task(), new AgentLimits(null, 2));

        Assert.Equal("ffs1.step-token", env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("http://host.docker.internal:8490/model", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal(("anthropic", "true"), (env["COPILOT_PROVIDER_TYPE"], env["COPILOT_OFFLINE"]));
        Assert.Equal(("/ff/home", CopilotCliRuntime.Home), (env["HOME"], env["COPILOT_HOME"]));
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "COPILOT_PROVIDER_BEARER_TOKEN" })
            Assert.DoesNotContain(name, env.Keys);
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
            Assert.DoesNotContain("allow-all", line, StringComparison.Ordinal);
            Assert.DoesNotContain("yolo", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_continuation_resumes_the_same_session()
    {
        var id = CopilotCliRuntime.SessionId("run-1/implement/1");
        Assert.Contains($"'--session-id' '{id}'", Runtime.CommandLine(Task(), "/p", false), StringComparison.Ordinal);
        Assert.Contains($"'--resume' '{id}'", Runtime.CommandLine(Task(), "/p", true), StringComparison.Ordinal);
        Assert.True(Guid.TryParse(id, out _));
    }

    const string Completed = """
        {"type":"assistant.message","data":{"content":"","toolRequests":[{"name":"create"}]}}
        {"type":"tool.execution_start","data":{"toolName":"create","arguments":{"path":"/ff/work/hello.txt","file_text":"hi"}}}
        {"type":"tool.execution_start","data":{"toolName":"view","arguments":{"path":"/ff/work/a.txt"}}}
        {"type":"tool.execution_start","data":{"toolName":"bash","arguments":{"command":"ls"}}}
        {"type":"assistant.message","data":{"content":"Done."}}
        {"type":"result","sessionId":"x","exitCode":0}
        """;

    const string Usage = """{"modelMetrics":{"claude-haiku-4-5":{"usage":{"inputTokens":79817,"outputTokens":788}}}}""";

    [Fact]
    public void A_completed_session_reads_its_result_files_and_usage()
    {
        var t = CopilotCliRuntime.Parse(Completed, Usage);

        Assert.Equal(("Done.", null, 79817L, 788L), (t.Result, t.ApiError, t.InputTokens, t.OutputTokens));
        Assert.Equal(["/ff/work/hello.txt", "/ff/work/a.txt"], t.Files);
    }

    [Fact]
    public void A_model_that_cannot_be_reached_ends_resumably_and_a_refusal_is_a_configuration_fault()
    {
        const string unreachable = """
            {"type":"session.error","data":{"errorType":"query","message":"Failed to get response from the AI model; retried 5 times. Last error: tcp connect error"}}
            {"type":"result","exitCode":1}
            """;
        const string refused = """
            {"type":"session.error","data":{"errorType":"authentication","message":"Authentication failed with provider (HTTP 401).","statusCode":401}}
            {"type":"result","exitCode":1}
            """;
        var down = CopilotCliRuntime.Parse(unreachable, "");
        var no = CopilotCliRuntime.Parse(refused, "");

        Assert.Equal(SessionEnd.ModelUnreachable, CopilotCliRuntime.ApiEnd(down.ApiStatus));
        Assert.NotNull(down.ApiError);
        Assert.Equal(SessionEnd.ModelRefused, CopilotCliRuntime.ApiEnd(no.ApiStatus));
        Assert.Equal(SessionEnd.Failed, CopilotCliRuntime.ApiEnd(400));
    }

    [Fact]
    public void The_runtime_takes_only_its_model_executable_model_access_and_engine_tools_as_settings() =>
        Assert.Equal(["model", "executable", "model_access", "engine_tools"], Assert.Single(CopilotCliProviders.Factories).Settings);
}
