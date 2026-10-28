using System.Text.Json.Serialization;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// Who is asking for a credential. docs/01 section 4.6: the engine, or an agent
/// step. The actor is part of the audit record, so it is carried on the
/// credential too.
/// </summary>
public sealed record Actor(
    string Kind,
    string Id,
    string? RunId = null,
    string? StepId = null)
{
    /// <summary>The engine acting on its own behalf.</summary>
    public static Actor Engine(string id = "engine") => new("engine", id);

    /// <summary>An agent step acting inside a sandbox.</summary>
    public static AgentActorBuilder Agent(string id) => new(id);

    public override string ToString() => RunId is null
        ? $"{Kind}:{Id}"
        : $"{Kind}:{Id} (run {RunId}, step {StepId})";
}

/// <summary>Builds an agent actor with its run and step.</summary>
public sealed class AgentActorBuilder
{
    private readonly string id;

    internal AgentActorBuilder(string id) => this.id = id;

    public Actor ForStep(string runId, string stepId) => new("agent", id, runId, stepId);
}

/// <summary>
/// A short-lived credential. docs/04 section 4.6 and check R6: the credential
/// records its own expiry and its scope, so a caller can assert that it has
/// expired and that it is no wider than asked for.
/// </summary>
/// <param name="Id">A local identifier for this issuance. Not the secret.</param>
/// <param name="TargetSystem">What the credential is for, e.g. "github".</param>
/// <param name="Scope">The granted scope, in canonical form.</param>
/// <param name="Secret">The credential value. Never logged, never serialised.</param>
/// <param name="IssuedAt">When it was minted.</param>
/// <param name="ExpiresAt">When it stops working. From the remote, not guessed.</param>
/// <param name="Permissions">What the remote says it grants.</param>
/// <param name="Actor">Who asked for it.</param>
/// <param name="Refreshable">Whether <see cref="ICredentialBroker.RefreshAsync"/> can renew it.</param>
public sealed record Credential(
    string Id,
    string TargetSystem,
    string Scope,
    [property: JsonIgnore] string Secret,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Permissions,
    Actor Actor,
    bool Refreshable = true)
{
    /// <summary>How long the credential is valid for, as issued.</summary>
    public TimeSpan Ttl => ExpiresAt - IssuedAt;

    /// <summary>
    /// Whether the credential has expired. The assertion R6 needs. Takes the
    /// current time so a test can assert expiry without waiting an hour.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Whether the credential has expired, against the current clock.</summary>
    public bool IsExpired() => IsExpired(DateTimeOffset.UtcNow);

    /// <summary>How long until it expires. Negative once expired.</summary>
    public TimeSpan Remaining(DateTimeOffset now) => ExpiresAt - now;

    /// <summary>
    /// A log-safe rendering. The secret is replaced by its fingerprint, so two
    /// log lines about the same credential can be correlated without revealing
    /// it (docs/01 4.8).
    /// </summary>
    public string Describe() =>
        $"credential {Id} target={TargetSystem} scope={Scope} " +
        $"issued={IssuedAt:O} expires={ExpiresAt:O} ttl={Ttl.TotalMinutes:F0}m " +
        $"secret={SecretScrubber.Fingerprint(Secret)} permissions=[{string.Join(", ", Permissions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"))}]";

    public override string ToString() => Describe();
}

/// <summary>docs/04 section 4.6.</summary>
public interface ICredentialBroker : IProvider
{
    Task<ProviderResult<Credential>> IssueAsync(
        Actor actor, string targetSystem, string scope, TimeSpan ttl, CancellationToken ct);

    Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken ct);
}

/// <summary>
/// The credential broker capability record. docs/04 section 1: capabilities are
/// typed records, never strings.
/// </summary>
public sealed record CredentialBrokerCapabilities(
    string Mechanism,
    IReadOnlyList<string> TargetSystems,
    TimeSpan MaxTtl,
    bool Refreshable);
