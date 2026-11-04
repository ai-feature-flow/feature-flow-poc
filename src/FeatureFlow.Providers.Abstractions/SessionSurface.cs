namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// A file the person writes in a session (docs/03 section 2.3, <c>session</c>): its path in the workspace folder, the
/// artifact it becomes on submission and that artifact's type, and the artifact it is seeded from, if any.
/// </summary>
public sealed record SessionFile(string Path, string Artifact, string? Type, string? SeedRef = null);

/// <summary>
/// What a session's person works on (docs/04 section 4.4): a branch the engine made in a named repository, at
/// <see cref="Revision"/>, a folder in it, the files to write, the rendered prompt, the context (an artifact reference),
/// and <see cref="ChecksFrom"/>, the later steps whose checks the person may run before submitting. <see cref="Person"/>
/// is the authenticated actor the session is for.
/// </summary>
public sealed record SessionWorkspace(
    string SessionId,
    string RunId,
    string WaitId,
    string Person,
    RepoRef Repository,
    string Branch,
    string Revision,
    string Folder,
    IReadOnlyList<SessionFile> Files,
    string Prompt,
    string? ContextRef,
    IReadOnlyList<string> ChecksFrom);

/// <summary>How the person reaches a prepared session: instructions for their own agent tool (a CLI command, the engine MCP server), and a link where the surface has one.</summary>
public sealed record SessionHandle(string SessionId, string Instructions, Uri? Url = null);

/// <summary>
/// The session surface (docs/06 section 3.11). Version 1 hands off to the person's own agent tool on their machine
/// (RW18). Preparing is idempotent by <see cref="SessionWorkspace.SessionId"/>. Checks and submission go through the
/// API, not the surface.
/// </summary>
public interface ISessionSurfaceProvider : IProvider
{
    Task<ProviderResult<SessionHandle>> PrepareAsync(SessionWorkspace workspace, CancellationToken cancellationToken);
}
