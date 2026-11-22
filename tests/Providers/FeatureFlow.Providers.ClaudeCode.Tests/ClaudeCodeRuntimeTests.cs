using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.ClaudeCode.Tests;

/// <summary>
/// What is Claude Code's own beyond the runtime contract (<see cref="ClaudeCodeRuntimeContract"/>): settings that only the
/// engine supplies and that allow no other hooks, its fixed defaults, and its settings keys.
/// </summary>
public sealed class ClaudeCodeRuntimeTests
{
    static readonly ClaudeCodeRuntime Runtime = new(new ClaudeCodeOptions("haiku"),
        () => throw new InvalidOperationException("no sandbox in this test"), () => throw new InvalidOperationException("no store in this test"));

    static AgentTask Task(string writeAccess = "branch") =>
        new(new StepIdentity("run-1", "t", "implement", 1, "run-1/implement/1"), "implementer", "Do it", new Dictionary<string, string>(),
            new StepContract([], new Dictionary<string, string>(), [], new Dictionary<string, string>()))
        {
            Tools = ["read", "search", "edit", "shell"],
            WriteAccess = writeAccess,
            Engine = new EngineEndpoints(["http://host.docker.internal:8490"], "http://host.docker.internal:8490/model", "ffs1.step-token"),
            Paths = AgentPaths.Default,
            Questions = true,
        };

    /// <summary>LOG 91: Claude Code's retries, the hook's hold and the client's retry are fixed defaults, not settings; L5: no <c>engine_tools</c>.</summary>
    [Fact]
    public void The_runtime_takes_only_its_model_executable_and_model_access_as_settings()
    {
        Assert.Equal(["model", "executable", "model_access"], Assert.Single(ClaudeCodeProviders.Factories).Settings);
        var defaults = new ClaudeCodeOptions("haiku");
        Assert.Equal((null, TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(10)), (defaults.MaxRetries, defaults.Hold, defaults.Retry));
        Assert.DoesNotContain("CLAUDE_CODE_MAX_RETRIES", Runtime.Environment(Task(), new AgentLimits(null, 2)).Keys);
    }

    /// <summary>A repository's own settings, hooks or MCP servers are not loaded: only the engine's.</summary>
    [Fact]
    public void The_command_line_loads_only_the_engine_s_settings_and_mcp_server()
    {
        var line = Runtime.CommandLine(Task(), "/ff/agent/prompt-1.md", resume: false);
        Assert.Contains("'--setting-sources' 'user'", line);
        Assert.Contains("'--strict-mcp-config' '--mcp-config' '/ff/agent/mcp.json'", line);
        Assert.DoesNotContain("mcp__engine__checkpoint", Runtime.CommandLine(Task("none"), "p", false));
    }

    /// <summary>The planning tools are allowed, and offered by the sandbox client (<c>FF_PLANNING</c>), only to a step with <c>planning</c>.</summary>
    [Fact]
    public void Only_a_step_with_planning_is_allowed_the_planning_tools()
    {
        var planning = Task() with { Planning = new Model.Plans.PlanningGrant(["self"], "required") };
        Assert.Contains("mcp__engine__report,mcp__engine__ask_human,mcp__engine__checkpoint,mcp__engine__inspect_plan,mcp__engine__propose_patch'",
            Runtime.CommandLine(planning, "p", false));
        Assert.Equal("true", Runtime.Environment(planning, new AgentLimits(null, 2))["FF_PLANNING"]);
        Assert.DoesNotContain("inspect_plan", Runtime.CommandLine(Task(), "p", false));
        Assert.DoesNotContain("propose_patch", Runtime.CommandLine(Task(), "p", false));
        Assert.Equal("false", Runtime.Environment(Task(), new AgentLimits(null, 2))["FF_PLANNING"]);
    }

    /// <summary>Review finding 7: hooks come from the root-owned managed settings, and no other source's hooks run.</summary>
    [Fact]
    public void Hooks_are_managed_settings_that_allow_no_other_hooks()
    {
        Assert.True(Runtime.Settings()["allowManagedHooksOnly"]!.GetValue<bool>());
        Assert.StartsWith("/etc/claude-code/", ClaudeCodeRuntime.ManagedSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("--settings", Runtime.CommandLine(Task(), "p", false));
    }
}
