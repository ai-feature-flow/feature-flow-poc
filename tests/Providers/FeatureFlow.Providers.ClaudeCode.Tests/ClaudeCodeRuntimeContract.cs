using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.ClaudeCode.Tests;

/// <summary>
/// The agent runtime contract (F12, LOG 447) run against the Claude Code runtime, with a stand-in for <c>claude</c>: it reads
/// the managed settings and the command line as Claude Code does, prints stream-json as Claude Code 2.1 did (an
/// <c>assistant</c> record per turn, a <c>result</c> record with usage or the API error), and sends Claude Code's hook input.
/// </summary>
public sealed class ClaudeCodeRuntimeContract : AgentRuntimeContract
{
    protected override IAgentRuntimeProvider CreateRuntime(ISandboxProvider sandboxes, IArtifactStoreProvider store) =>
        new ClaudeCodeRuntime(new ClaudeCodeOptions("haiku"), () => sandboxes, () => store);

    protected override IReadOnlyList<HookSetting> Hooks(CliInvocation invocation)
    {
        var settings = JsonNode.Parse(invocation.Files[ClaudeCodeRuntime.ManagedSettings])!;
        return settings["hooks"]!.AsObject().SelectMany(e => e.Value!.AsArray().SelectMany(m => m!["hooks"]!.AsArray()))
            .Select(h => new HookSetting(h!["command"]!.GetValue<string>(), h["timeout"]!.GetValue<int>())).ToList();
    }

    protected override TimeSpan McpToolTimeout(CliInvocation invocation) =>
        TimeSpan.FromMilliseconds(long.Parse(invocation.Environment["MCP_TOOL_TIMEOUT"], System.Globalization.CultureInfo.InvariantCulture));

    static string? After(CliInvocation invocation, string option) =>
        invocation.Arguments.Select((a, i) => (a, i)).Where(x => x.a == option).Select(x => invocation.Arguments[x.i + 1]).FirstOrDefault();

    protected override (string? Id, bool Resumes) Session(CliInvocation invocation) =>
        After(invocation, "--resume") is { } resumed ? (resumed, true) : (After(invocation, "--session-id"), false);

    protected override IReadOnlyList<string> OfferedTools(CliInvocation invocation) => After(invocation, "--tools")!.Split(',');

    /// <summary>The tool Claude Code calls for an action, with its input.</summary>
    static (string Tool, JsonObject Input) Tool(CliAction action) => action.Class switch
    {
        ToolClass.Read => ("Read", new JsonObject { ["file_path"] = action.Path }),
        ToolClass.Search => ("Grep", new JsonObject { ["pattern"] = "TODO" }),
        ToolClass.Edit => ("Edit", new JsonObject { ["file_path"] = action.Path, ["old_string"] = "a", ["new_string"] = "b" }),
        ToolClass.Shell => ("Bash", new JsonObject { ["command"] = action.Command }),
        ToolClass.Engine => ("mcp__engine__report", new JsonObject { ["outcome"] = "done", ["summary"] = "x" }),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    protected override CliRun Play(CliInvocation invocation, CliScript script)
    {
        var lines = script.Actions.Select(a => Tool(a)).SelectMany((t, i) => new[]
        {
            new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{i}", ["name"] = t.Tool, ["input"] = t.Input }) },
            },
            new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = $"toolu_{i}", ["content"] = "ok" }) },
            },
        }).Append(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = script.Text }) },
        }).Append(new JsonObject
        {
            ["type"] = "result",
            ["terminal_reason"] = script.ApiError is null ? "completed" : "api_error",
            ["result"] = script.ApiError is null ? script.Text : "API Error",
            ["api_error"] = script.ApiError,
            ["api_error_status"] = script.ApiStatus,
            ["usage"] = new JsonObject { ["input_tokens"] = script.InputTokens, ["output_tokens"] = script.OutputTokens },
        });
        return new CliRun(string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n");
    }

    protected override JsonObject HookInput(CliAction action)
    {
        var (tool, input) = Tool(action);
        return new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input };
    }

    protected override JsonObject HookInputForTool(string tool) =>
        new() { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = new JsonObject { ["file_path"] = "/ff/work/a.txt" } };

    /// <summary>
    /// Claude Code 2.1's tools by what they do: reading (a todo list writes no file), searching, editing files, and the
    /// shell, with the tools that come with them (a notebook read, a background shell's output and kill).
    /// </summary>
    protected override IReadOnlyDictionary<string, string> CliTools { get; } = new Dictionary<string, string>
    {
        ["Read"] = ToolClass.Read, ["LS"] = ToolClass.Read, ["TodoWrite"] = ToolClass.Read, ["NotebookRead"] = ToolClass.Read,
        ["Grep"] = ToolClass.Search, ["Glob"] = ToolClass.Search,
        ["Edit"] = ToolClass.Edit, ["Write"] = ToolClass.Edit, ["MultiEdit"] = ToolClass.Edit, ["NotebookEdit"] = ToolClass.Edit,
        ["Bash"] = ToolClass.Shell, ["BashOutput"] = ToolClass.Shell, ["KillShell"] = ToolClass.Shell, ["KillBash"] = ToolClass.Shell,
    };

    /// <summary>
    /// Claude Code blocks a call whose hook exits 2 and shows the agent its standard error; a hook that prints
    /// <c>continue: false</c> stops the session before the call runs.
    /// </summary>
    protected override (bool Blocked, string? Shown) ReadReply(HookReply reply)
    {
        if (reply.Exit == 2)
            return (true, reply.Error?.Trim());
        return reply is { Exit: 0, Output: { } output } && JsonNode.Parse(output) is { } decision && decision["continue"]?.GetValue<bool>() == false
            ? (true, decision["stopReason"]?.ToString())
            : (false, null);
    }
}
