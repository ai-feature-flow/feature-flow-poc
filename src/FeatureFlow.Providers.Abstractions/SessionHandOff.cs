namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// Version 1's session surface (RW18, docs/06 section 3.11): a hand-off to the person's own agent tool on their machine.
/// The engine has made the branch; the person checks it out, fetches the session (prompt, drafts, context), runs the
/// checks, commits, pushes and submits, through the CLI or the engine MCP server it serves, both authenticated as the
/// person through the API.
/// </summary>
public static class SessionHandOff
{
    public static string Instructions(SessionWorkspace w)
    {
        var checks = w.ChecksFrom.Count == 0
            ? "  (this session names no checks)\n"
            : string.Concat(w.ChecksFrom.Select(c => $"  ff session check {w.SessionId} {c}\n"));
        var files = string.Join(", ", w.Files.Select(f => $"{(w.Folder.Length == 0 ? "" : w.Folder + "/")}{f.Path} ({f.Artifact})"));
        return $"""
            Session {w.SessionId}: write {files} on branch {w.Branch} of {w.Repository.FullName}.
              git fetch origin {w.Branch} && git switch {w.Branch}
              ff session fetch {w.SessionId} --out .ff-session      # the prompt, drafts and context
            Before submitting, commit and push, then run the later steps' checks on that commit:
            {checks}  ff session submit {w.SessionId}                # submits the commit at HEAD
            Or give your agent tool the engine's MCP server: ff session mcp (tools session_fetch, session_check, session_submit).
            """;
    }
}
