using System.Text.Json;
using System.Text.RegularExpressions;

namespace FeatureFlow.Engine.Agent;

/// <summary>A hook's answer: allow the tool call, or deny it with a reason the agent reads.</summary>
public sealed record HookDecision(string Decision, string? Reason = null)
{
    public static readonly HookDecision Allow = new("allow");

    public static HookDecision Deny(string reason) => new("deny", reason);
}

/// <summary>
/// Policy on each tool call (docs/02 section 4.1), from the step's token alone: deny writes for a role with no
/// write access, deny <c>git push</c> (the engine pushes, F5), deny edits to the repository's <c>.git</c> folder,
/// and deny a tool outside the role's tool classes. Write access is also enforced by the sandbox (a read-only
/// checkout), so a runtime without hooks keeps the control (docs/04 section 3.7).
/// <para>
/// Claude Code and Copilot CLI send the same hook input with the same tool names (Copilot maps its own: <c>view</c> is
/// <c>Read</c>, <c>create</c> is <c>Write</c>), except that Copilot names a file <c>path</c>, not <c>file_path</c>, and an
/// engine tool <c>engine-report</c>, not <c>mcp__engine__report</c>.
/// </para>
/// </summary>
public static partial class HookPolicy
{
    /// <summary>The runtime's tools for each abstract tool class a role lists (LOG 65). Tools of the engine's MCP server are always allowed.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses = new Dictionary<string, IReadOnlyList<string>>
    {
        ["read"] = ["Read", "LS", "NotebookRead", "TodoWrite"],
        ["search"] = ["Grep", "Glob"],
        ["edit"] = ["Edit", "Write", "MultiEdit", "NotebookEdit"],
        ["shell"] = ["Bash", "BashOutput", "KillShell", "KillBash"],
    };

    static readonly HashSet<string> Writing = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    // The command by name, wherever it lives (/usr/bin/git), with any options before the subcommand.
    [GeneratedRegex(@"(^|[\s;&|(`])(\S*/)?git(\s+-\S+(\s+\S+)?)*\s+push\b")]
    private static partial Regex GitPush();

    [GeneratedRegex(@"(^|/)\.git(/|$)")]
    private static partial Regex GitFolder();

    public static HookDecision PreToolUse(StepClaims claims, string tool, JsonElement input)
    {
        if (tool.StartsWith("mcp__engine__", StringComparison.Ordinal) || tool.StartsWith("engine-", StringComparison.Ordinal))
            return HookDecision.Allow;
        var allowed = claims.Tools.SelectMany(c => ToolClasses.GetValueOrDefault(c, [])).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(tool))
            return HookDecision.Deny($"{tool} is not one of this role's tools");
        if (Writing.Contains(tool) && claims.WriteAccess == "none")
            return HookDecision.Deny("this role cannot change files: its checkout is read-only");
        if (Writing.Contains(tool) && (Text(input, "file_path") ?? Text(input, "path")) is { } path && GitFolder().IsMatch(path))
            return HookDecision.Deny("the repository's .git folder is the engine's; do not edit it");
        if (tool == "Bash" && Text(input, "command") is { } command)
        {
            if (GitPush().IsMatch(command))
                return HookDecision.Deny("never push: commit your work and the engine pushes it");
            if (command.Contains(".git/config", StringComparison.Ordinal) || command.Contains(".git/hooks", StringComparison.Ordinal))
                return HookDecision.Deny("the repository's .git folder is the engine's; do not edit it");
        }

        return HookDecision.Allow;
    }

    /// <summary>The file a tool call opened, for the record of files read (docs/04 section 3.6), or null.</summary>
    public static string? FileOpened(string tool, JsonElement input) =>
        tool is "Read" or "NotebookRead" or "Edit" or "MultiEdit" or "Write" ? Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "path") : null;

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
