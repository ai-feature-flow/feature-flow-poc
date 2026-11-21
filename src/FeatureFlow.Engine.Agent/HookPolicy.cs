using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// A hook's answer: allow the tool call, or deny it with a reason the agent reads. <see cref="Reply"/> is how the
/// step's runtime tells its CLI of a denial, which the hook command prints and exits with (LOG A5b-1).
/// </summary>
public sealed record HookDecision(string Decision, string? Reason = null, HookReply? Reply = null)
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
/// The policy sees tool classes only: the step's runtime reads its CLI's hook input as a <see cref="ToolCall"/>
/// (<see cref="IAgentHooks"/>; LOG 65, A5b-1). A tool the runtime does not map has no class and is denied.
/// </para>
/// </summary>
public static partial class HookPolicy
{
    // The command by name, wherever it lives (/usr/bin/git), with any options before the subcommand.
    [GeneratedRegex(@"(^|[\s;&|(`])(\S*/)?git(\s+-\S+(\s+\S+)?)*\s+push\b")]
    private static partial Regex GitPush();

    [GeneratedRegex(@"(^|/)\.git(/|$)")]
    private static partial Regex GitFolder();

    public static HookDecision PreToolUse(StepClaims claims, ToolCall call)
    {
        if (call.Class == ToolClass.Engine)
            return HookDecision.Allow;
        if (call.Class is null || !claims.Tools.Contains(call.Class))
            return HookDecision.Deny($"{call.Tool} is not one of this role's tools");
        if (call.Class == ToolClass.Edit && claims.WriteAccess == "none")
            return HookDecision.Deny("this role cannot change files: its checkout is read-only");
        if (call.Class == ToolClass.Edit && call.Path is { } path && GitFolder().IsMatch(path))
            return HookDecision.Deny("the repository's .git folder is the engine's; do not edit it");
        if (call.Class == ToolClass.Shell && call.Command is { } command)
        {
            if (GitPush().IsMatch(command))
                return HookDecision.Deny("never push: commit your work and the engine pushes it");
            if (command.Contains(".git/config", StringComparison.Ordinal) || command.Contains(".git/hooks", StringComparison.Ordinal))
                return HookDecision.Deny("the repository's .git folder is the engine's; do not edit it");
        }

        return HookDecision.Allow;
    }

    /// <summary>The file a tool call opened, for the record of files read (docs/04 section 3.6), or null.</summary>
    public static string? FileOpened(ToolCall call) => call.Class is ToolClass.Read or ToolClass.Edit ? call.Path : null;
}
