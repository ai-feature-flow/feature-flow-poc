using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// The version 1 session surface (RW18, docs/06 section 3.11), kind <c>local</c>: a hand-off to the person's own agent tool
/// on their machine. Preparing writes nothing; the handle is the instructions <see cref="SessionHandOff"/> gives, the same
/// for the same session every time.
/// </summary>
public sealed class LocalSessionSurface : ISessionSurfaceProvider
{
    public ProviderDescription Describe() => new("session_surface", "local", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<SessionHandle>> PrepareAsync(SessionWorkspace workspace, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(workspace.SessionId)
            ? ProviderResult<SessionHandle>.Failure("validation_failed", "a session needs an id")
            : ProviderResult<SessionHandle>.Success(new SessionHandle(workspace.SessionId, SessionHandOff.Instructions(workspace))));
}
