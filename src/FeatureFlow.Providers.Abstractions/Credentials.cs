namespace FeatureFlow.Providers.Abstractions;

/// <summary>Who a credential is for (docs/06 section 3.13): the engine, an agent step, or a model proxy.</summary>
public sealed record Actor(string Kind, string Id)
{
    public static readonly Actor Engine = new("engine", "engine");
}

/// <summary>
/// What a credential may do: repositories and permissions such as <c>contents:write</c>. Modelled, not a free
/// string, so a broker can decide whether it may grant it.
/// </summary>
public sealed record CredentialScope(IReadOnlyList<string> Repositories, IReadOnlyDictionary<string, string> Permissions)
{
    public static CredentialScope Read(params string[] repositories) => new(repositories, new Dictionary<string, string> { ["contents"] = "read" });

    public static CredentialScope Write(params string[] repositories) => new(repositories, new Dictionary<string, string> { ["contents"] = "write" });

    public override string ToString() =>
        $"[{string.Join(", ", Repositories)}] {string.Join(" ", Permissions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"))}";
}

/// <summary>An issued credential. <see cref="Secret"/> is never logged or recorded; <see cref="ToString"/> leaves it out.</summary>
public sealed record Credential(string TargetSystem, CredentialScope Scope, string Secret, DateTimeOffset ExpiresAt, Actor Actor)
{
    public override string ToString() => $"credential for {Actor.Kind} on {TargetSystem} {Scope}, expires {ExpiresAt:O}";
}

/// <summary>
/// The credential broker (docs/06 section 3.13). It refuses a request wider than it may grant, with
/// <c>forbidden</c>; it never silently narrows one.
/// </summary>
public interface ICredentialBroker : IProvider
{
    Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl, CancellationToken cancellationToken);

    Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken cancellationToken);
}
