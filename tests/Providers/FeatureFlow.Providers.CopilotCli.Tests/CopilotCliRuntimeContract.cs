using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.CopilotCli.Tests;

/// <summary>
/// The agent runtime contract (F12, LOG A5b-4) run against the Copilot CLI runtime, with a stand-in for <c>copilot</c>: it reads
/// <c>COPILOT_HOME</c>'s settings, the MCP configuration and the command line as Copilot does, prints the JSON lines Copilot CLI
/// 1.0.105 printed (an <c>assistant.message</c> per turn, <c>tool.execution_start</c> by its own tool names, <c>session.error</c>),
/// writes its usage file, and sends the hook input Copilot sends: Claude Code's tool names, the file as <c>path</c>, the
/// engine's tools as <c>engine-*</c> (LOG 293).
/// </summary>
public sealed class CopilotCliRuntimeContract : AgentRuntimeContract
{
    protected override IAgentRuntimeProvider CreateRuntime(ISandboxProvider sandboxes, IArtifactStoreProvider store) =>
        new CopilotCliRuntime(new CopilotCliOptions("claude-haiku-4-5"), () => sandboxes, () => store);

    protected override IReadOnlyList<HookSetting> Hooks(CliInvocation invocation)
    {
        var settings = JsonNode.Parse(invocation.Files[invocation.Environment["COPILOT_HOME"] + "/settings.json"])!;
        return settings["hooks"]!.AsObject().SelectMany(e => e.Value!.AsArray())
            .Select(h => new HookSetting(h!["command"]!.GetValue<string>(), h["timeoutSec"]!.GetValue<int>())).ToList();
    }

    protected override TimeSpan McpToolTimeout(CliInvocation invocation)
    {
        var config = JsonNode.Parse(invocation.Files[After(invocation, "--additional-mcp-config")!.TrimStart('@')])!;
        return TimeSpan.FromMilliseconds(config["mcpServers"]!["engine"]!["timeout"]!.GetValue<long>());
    }

    static string? After(CliInvocation invocation, string option) =>
        invocation.Arguments.Select((a, i) => (a, i)).Where(x => x.a == option).Select(x => invocation.Arguments[x.i + 1]).FirstOrDefault();

    protected override (string? Id, bool Resumes) Session(CliInvocation invocation) =>
        After(invocation, "--resume") is { } resumed ? (resumed, true) : (After(invocation, "--session-id"), false);

    /// <summary>The values after <c>--available-tools</c>, to the next option, without the engine's server.</summary>
    protected override IReadOnlyList<string> OfferedTools(CliInvocation invocation) =>
        invocation.Arguments.SkipWhile(a => a != "--available-tools").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Where(a => a != "engine").ToList();

    /// <summary>Copilot's own tool for an action, with its arguments.</summary>
    static (string Tool, JsonObject Arguments) Tool(CliAction action) => action.Class switch
    {
        ToolClass.Read => ("view", new JsonObject { ["path"] = action.Path }),
        ToolClass.Search => ("grep", new JsonObject { ["pattern"] = "TODO" }),
        ToolClass.Edit => ("edit", new JsonObject { ["path"] = action.Path, ["old_str"] = "a", ["new_str"] = "b" }),
        ToolClass.Shell => ("bash", new JsonObject { ["command"] = action.Command }),
        ToolClass.Engine => ("engine-report", new JsonObject { ["outcome"] = "done", ["summary"] = "x" }),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The name Copilot's hooks give its own tool (LOG 293: <c>view</c> is <c>Read</c>, <c>create</c> is <c>Write</c>).</summary>
    static string HookName(string tool) => tool switch
    {
        "view" => "Read",
        "grep" => "Grep",
        "glob" => "Glob",
        "create" => "Write",
        "edit" => "Edit",
        "bash" => "Bash",
        _ => tool,
    };

    protected override CliRun Play(CliInvocation invocation, CliScript script)
    {
        var lines = new List<JsonObject>();
        foreach (var (tool, arguments) in script.Actions.Select(Tool))
        {
            lines.Add(new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = "", ["toolRequests"] = new JsonArray(new JsonObject { ["name"] = tool }) } });
            lines.Add(new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolName"] = tool, ["arguments"] = arguments } });
        }

        lines.Add(new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = script.Text } });
        if (script.ApiError is not null)
            lines.Add(new JsonObject
            {
                ["type"] = "session.error",
                ["data"] = new JsonObject { ["errorType"] = script.ApiStatus is null ? "query" : "api", ["message"] = script.ApiError, ["statusCode"] = script.ApiStatus },
            });
        lines.Add(new JsonObject { ["type"] = "result", ["exitCode"] = script.Exit });
        var usage = new JsonObject
        {
            ["modelMetrics"] = new JsonObject
            {
                ["claude-haiku-4-5"] = new JsonObject { ["usage"] = new JsonObject { ["inputTokens"] = script.InputTokens, ["outputTokens"] = script.OutputTokens } },
            },
        };
        return new CliRun(string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n",
            new Dictionary<string, string> { [After(invocation, "--usage-output-file")!] = usage.ToJsonString() });
    }

    protected override JsonObject HookInput(CliAction action)
    {
        var (tool, arguments) = Tool(action);
        return new JsonObject { ["tool_name"] = HookName(tool), ["tool_input"] = arguments };
    }

    protected override JsonObject HookInputForTool(string tool) =>
        new() { ["tool_name"] = HookName(tool), ["tool_input"] = new JsonObject { ["path"] = "/ff/work/a.txt" } };

    /// <summary>
    /// Copilot CLI 1.0.105's tools by what they do, by the names its hooks give them (Claude Code's) and by its own:
    /// viewing, searching, creating and editing files, and the shell.
    /// </summary>
    protected override IReadOnlyDictionary<string, string> CliTools { get; } = new Dictionary<string, string>
    {
        ["Read"] = ToolClass.Read, ["view"] = ToolClass.Read,
        ["Grep"] = ToolClass.Search, ["Glob"] = ToolClass.Search, ["grep"] = ToolClass.Search, ["glob"] = ToolClass.Search,
        ["Write"] = ToolClass.Edit, ["Edit"] = ToolClass.Edit, ["create"] = ToolClass.Edit, ["edit"] = ToolClass.Edit,
        ["Bash"] = ToolClass.Shell, ["bash"] = ToolClass.Shell,
    };

    /// <summary>Copilot blocks a call whose hook exits 2 but shows the agent nothing; its JSON decision blocks it with the reason (LOG 293).</summary>
    protected override (bool Blocked, string? Shown) ReadReply(HookReply reply)
    {
        if (reply.Exit == 2)
            return (true, null);
        return reply is { Exit: 0, Output: { } output } && JsonNode.Parse(output) is { } decision && decision["permissionDecision"]?.ToString() == "deny"
            ? (true, decision["permissionDecisionReason"]?.ToString())
            : (false, null);
    }
}
