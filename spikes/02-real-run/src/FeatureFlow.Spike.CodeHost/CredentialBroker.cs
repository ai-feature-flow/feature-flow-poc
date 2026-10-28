using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// The credential broker seam. docs/04 section 12, docs/05 section 12.
///
/// <para>
/// This file defines the <b>minimal shape the code host needs</b>, so the code
/// host compiles and runs today. It is a seam, not a second implementation:
/// the sibling agent's <c>FeatureFlow.Spike.Providers</c> owns
/// <c>ICredentialBroker</c>, <c>Actor</c> and <c>Credential</c>. When those
/// types land on disk, delete this file and the code host binds to them
/// unchanged — nothing in <c>GitHubCodeHostProvider</c> references these
/// declarations by name except through <see cref="ITokenFactory"/>.
/// </para>
///
/// <para>
/// The code host depends on <see cref="ITokenFactory"/> only, which is the
/// narrowest thing that can express "give me a token for this repository".
/// <see cref="BrokerTokenFactory"/> adapts the full broker interface down to
/// it, so the provider never sees actor, scope or TTL policy.
/// </para>
/// </summary>

/// <summary>
/// Who is asking. The engine acts as the bot identity; an agent step acts as
/// itself, scoped to one repository (docs/01 section 4.6).
/// </summary>
public enum ActorKind
{
    Engine,
    AgentStep,
}

public sealed record Actor(ActorKind Kind, string Id)
{
    public static Actor Engine { get; } = new(ActorKind.Engine, "engine");

    public static Actor ForStep(string stepId) => new(ActorKind.AgentStep, stepId);
}

/// <summary>
/// A short-lived credential. <see cref="ExpiresAt"/> is what makes R6
/// checkable: the credential expires, and the provider must refresh it.
/// </summary>
public sealed record Credential(
    string Token,
    DateTimeOffset ExpiresAt,
    string? Scope = null)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Refresh this far before expiry, so a call in flight does not
    /// outlive its own credential.</summary>
    public static TimeSpan RefreshMargin { get; } = TimeSpan.FromMinutes(5);
}

/// <summary>docs/04 section 12.</summary>
public interface ICredentialBroker : IProvider
{
    Task<ProviderResult<Credential>> IssueAsync(
        Actor actor, string targetSystem, string scope, TimeSpan ttl, CancellationToken ct);

    Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken ct);
}

/// <summary>
/// The only credential dependency the code host has. A <c>Func</c> would do,
/// but a named interface keeps the intent visible at the constructor and gives
/// the harness somewhere to hang a failing or counting implementation.
/// </summary>
public interface ITokenFactory
{
    Task<ProviderResult<Credential>> GetTokenAsync(RepoRef repo, CancellationToken ct);
}

/// <summary>
/// Adapts <see cref="ICredentialBroker"/> to <see cref="ITokenFactory"/>. The
/// code host asks for a token per repository; the broker decides identity,
/// scope and TTL.
/// </summary>
public sealed class BrokerTokenFactory : ITokenFactory
{
    private readonly ICredentialBroker broker;
    private readonly Actor actor;
    private readonly TimeSpan ttl;

    public BrokerTokenFactory(ICredentialBroker broker, Actor? actor = null, TimeSpan? ttl = null)
    {
        this.broker = broker;
        this.actor = actor ?? Actor.Engine;
        this.ttl = ttl ?? TimeSpan.FromMinutes(60);
    }

    public Task<ProviderResult<Credential>> GetTokenAsync(RepoRef repo, CancellationToken ct) =>
        broker.IssueAsync(actor, "github", repo.FullName, ttl, ct);
}

/// <summary>
/// A token factory for local verification: wraps a fixed token. The value is
/// supplied by the caller (minted by <c>tools/gh_app_token.py</c>), never read
/// from configuration — floor F2 and the "credentials through the broker only"
/// rule both hold.
/// </summary>
public sealed class StaticTokenFactory : ITokenFactory
{
    private readonly Credential credential;

    public StaticTokenFactory(string token, DateTimeOffset? expiresAt = null)
    {
        credential = new Credential(token, expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(60));
    }

    public Task<ProviderResult<Credential>> GetTokenAsync(RepoRef repo, CancellationToken ct) =>
        Task.FromResult(ProviderResult<Credential>.Ok(credential));
}
