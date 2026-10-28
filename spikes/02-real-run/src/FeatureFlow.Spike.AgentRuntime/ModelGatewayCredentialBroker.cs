using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// A credential broker for the model endpoint.
///
/// docs/04 section 1: "No provider reads credentials from configuration. All go
/// through <see cref="ICredentialBroker"/>." docs/05 section 3: "Model
/// credentials are short-lived and scoped to the step, issued by the credential
/// broker, preferably through a model gateway."
///
/// Spike scope. The real deployment issues a per-step key from a model gateway,
/// which is also the second source of truth for cost (docs/01 5.5). Here the
/// value is resolved by reference from the secrets provider, so no credential
/// value is ever in configuration or in the repository (floor F2).
///
/// The credential is refused rather than widened, exactly as the GitHub App
/// broker does: a scope this broker cannot grant is <c>forbidden</c>, class
/// <see cref="ErrorClass.Work"/>.
/// </summary>
public sealed class ModelGatewayCredentialBroker : ICredentialBroker
{
    public const string TargetSystemName = "anthropic";

    /// <summary>docs/05 section 12: IssueAsync and RefreshAsync time out at 15s.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The longest TTL this broker will grant.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    private readonly ISecretsProvider secrets;
    private readonly SecretScrubber scrubber;
    private readonly SecretReference keyRef;
    private readonly IReadOnlyList<GrantableScope> grantable;
    private readonly Func<DateTimeOffset> clock;

    private int issued;

    public ModelGatewayCredentialBroker(
        ISecretsProvider secrets,
        SecretScrubber? scrubber = null,
        SecretReference? keyRef = null,
        IEnumerable<GrantableScope>? grantable = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.scrubber = scrubber ?? new SecretScrubber();
        this.keyRef = keyRef ?? new SecretReference("secrets", "model-api-key");
        this.grantable = grantable?.ToList() ?? DefaultGrantableScopes();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The default grantable set: model invocation on the fixture repository.
    /// The repository is part of the scope because docs/01 4.6 says a credential
    /// is scoped to one repository, and R6 checks exactly that.
    /// </summary>
    public static IReadOnlyList<GrantableScope> DefaultGrantableScopes() => new[]
    {
        GrantableScope.ForRepository(
            "ai-feature-flow/feature-flow-example",
            "model:write"),
    };

    /// <summary>How many credentials this broker has issued. Safe to log.</summary>
    public int IssuedCount => issued;

    public ProviderDescriptor Describe() => new(
        Name: "model-gateway",
        Version: "0.1.0",
        Capabilities: new object[]
        {
            new CredentialBrokerCapabilities(
                Mechanism: "model-gateway-api-key",
                TargetSystems: new[] { TargetSystemName },
                MaxTtl: MaxTtl,
                Refreshable: true),
        },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "feature-flow/schemas/credentialBroker/model-gateway/v1");

    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();

        var key = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (!key.IsOk)
            return ProviderResult<CheckResult>.Fail(key.Error!);

        // Only the fingerprint and the length are reported. The value is never
        // rendered (docs/01 4.8).
        return ProviderResult<CheckResult>.Ok(new CheckResult(
            Ok: true,
            Detail: $"model key {key.Value!.Describe()} fingerprint={SecretScrubber.Fingerprint(key.Value!.Value)}",
            Latency: clock() - started));
    }

    public async Task<ProviderResult<Credential>> IssueAsync(
        Actor actor, string targetSystem, string scope, TimeSpan ttl, CancellationToken ct)
    {
        if (actor is null)
            return ProviderResult<Credential>.Work("validation_failed");

        if (!string.Equals(targetSystem, TargetSystemName, StringComparison.OrdinalIgnoreCase))
            return ProviderResult<Credential>.Work("validation_failed", $"unknown target system '{targetSystem}'");

        if (ttl <= TimeSpan.Zero)
            return ProviderResult<Credential>.Work("validation_failed", "ttl must be positive");

        if (ttl > MaxTtl)
            return ProviderResult<Credential>.Work(
                "forbidden",
                $"cannot grant a ttl of {ttl.TotalMinutes:F0} min; the maximum is {MaxTtl.TotalMinutes:F0} min");

        CredentialScope requested;
        try
        {
            requested = CredentialScope.Parse(scope);
        }
        catch (FormatException)
        {
            return ProviderResult<Credential>.Work("validation_failed", $"scope '{scope}' is not well-formed");
        }

        // Refuse, never widen. Checked before the secret is resolved, so an
        // out-of-scope request never reaches the secrets provider.
        var grant = grantable.FirstOrDefault(g => g.Covers(requested));
        if (grant is null)
        {
            var ungranted = grantable
                .SelectMany(g => g.Ungranted(requested))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var detail = ungranted.Count > 0
                ? $"cannot grant [{string.Join(", ", ungranted)}] on {requested.Repository}"
                : $"{requested.Repository} is not a repository this broker may grant";

            return ProviderResult<Credential>.Work("forbidden", detail);
        }

        var key = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (!key.IsOk)
            return ProviderResult<Credential>.Fail(key.Error!);

        // Registered for scrubbing, so a later log line or record-store entry
        // that quotes the value is redacted (docs/01 4.8).
        scrubber.Register(key.Value!.Value);

        var now = clock();
        var id = $"model-{Interlocked.Increment(ref issued)}";

        return ProviderResult<Credential>.Ok(new Credential(
            Id: id,
            TargetSystem: TargetSystemName,
            Scope: requested.Text,
            Secret: key.Value!.Value,
            IssuedAt: now,
            ExpiresAt: now + ttl,
            Permissions: requested.Permissions.ToDictionary(p => p, _ => "granted", StringComparer.Ordinal),
            Actor: actor,
            Refreshable: true));
    }

    public Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken ct)
    {
        if (credential is null)
            return Task.FromResult(ProviderResult<Credential>.Work("validation_failed"));

        if (!credential.Refreshable)
            return Task.FromResult(ProviderResult<Credential>.Work("forbidden", "this credential is not refreshable"));

        return IssueAsync(
            credential.Actor,
            credential.TargetSystem,
            credential.Scope,
            credential.Ttl,
            ct);
    }
}
