using System.Globalization;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// The GitHub App credential broker. docs/04 section 4.6, docs/05 section 12.
///
/// The chain: a private key held outside the repository (floor F2) is resolved
/// through the secrets provider by reference, minted into a 10 minute App JWT,
/// and exchanged for an installation token that is scoped to one repository and
/// expires in about an hour.
///
/// Two rules the implementation is careful about:
///
/// - **Refuse, never widen.** A scope the broker cannot grant comes back as
///   `forbidden` with class <see cref="ErrorClass.Work"/>. It never silently
///   issues a wider credential, because a wider credential is indistinguishable
///   from a correct one at the call site.
/// - **The credential carries its own expiry.** Taken from the remote's
///   `expires_at`, not computed from the requested TTL, so the record states
///   when the credential actually stops working.
/// </summary>
public sealed class GitHubAppCredentialBroker : ICredentialBroker
{
    public const string TargetSystemName = "github";

    /// <summary>docs/05 section 12: IssueAsync and RefreshAsync both time out at 15s.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest TTL the broker will grant. GitHub installation tokens are
    /// always one hour, so a request for longer is refused rather than granted
    /// and then silently truncated.
    /// </summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    private readonly ISecretsProvider secrets;
    private readonly GitHubAppClient client;
    private readonly SecretScrubber scrubber;
    private readonly IReadOnlyList<GrantableScope> grantable;
    private readonly SecretReference appIdRef;
    private readonly SecretReference keyRef;
    private readonly SecretReference installationRef;
    private readonly Func<DateTimeOffset> clock;

    private int issued;

    public GitHubAppCredentialBroker(
        ISecretsProvider secrets,
        GitHubAppClient? client = null,
        SecretScrubber? scrubber = null,
        IEnumerable<GrantableScope>? grantable = null,
        SecretReference? appIdRef = null,
        SecretReference? keyRef = null,
        SecretReference? installationRef = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.client = client ?? new GitHubAppClient();
        this.scrubber = scrubber ?? new SecretScrubber();
        this.grantable = grantable?.ToList() ?? DefaultGrantableScopes();
        this.appIdRef = appIdRef ?? new SecretReference("secrets", "github-app-id");
        this.keyRef = keyRef ?? new SecretReference("secrets", "github-app-key");
        this.installationRef = installationRef ?? new SecretReference("secrets", "github-installation-id");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The default grantable set: one repository, contents:write, plus the
    /// permissions a branch-and-PR step needs. docs/01 4.6 says "one repository,
    /// minimum permissions (contents write)".
    /// </summary>
    public static IReadOnlyList<GrantableScope> DefaultGrantableScopes() => new[]
    {
        GrantableScope.ForRepository(
            "ai-feature-flow/feature-flow-poc",
            "contents:write",
            "pull_requests:write",
            "metadata:read"),
    };

    /// <summary>How many credentials this broker has issued. Safe to log.</summary>
    public int IssuedCount => issued;

    public ProviderDescriptor Describe() => new(
        Name: "github-app",
        Version: "0.1.0",
        Capabilities: new object[]
        {
            new CredentialBrokerCapabilities(
                Mechanism: "github-app-installation-token",
                TargetSystems: new[] { TargetSystemName },
                MaxTtl: MaxTtl,
                Refreshable: true),
        },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "feature-flow/schemas/credentialBroker/github-app/v1");

    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();

        var appId = await secrets.ResolveAsync(appIdRef, ct).ConfigureAwait(false);
        if (!appId.IsOk)
            return ProviderResult<CheckResult>.Fail(appId.Error!);

        var key = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (!key.IsOk)
            return ProviderResult<CheckResult>.Fail(key.Error!);

        try
        {
            var jwt = GitHubAppJwt.Mint(appId.Value!.Value, key.Value!.Value, clock());
            var app = await client.GetAppAsync(jwt, ct).ConfigureAwait(false);

            return app.IsOk
                ? ProviderResult<CheckResult>.Ok(new CheckResult(
                    Ok: true,
                    Detail: $"app {app.Value!.Name} (id {app.Value.Id})",
                    Latency: clock() - started))
                : ProviderResult<CheckResult>.Fail(app.Error!);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return ProviderResult<CheckResult>.Infrastructure("key_invalid");
        }
    }

    public async Task<ProviderResult<Credential>> IssueAsync(
        Actor actor, string targetSystem, string scope, TimeSpan ttl, CancellationToken ct)
    {
        if (actor is null)
            return ProviderResult<Credential>.Work("validation_failed");

        if (!string.Equals(targetSystem, TargetSystemName, StringComparison.OrdinalIgnoreCase))
            return ProviderResult<Credential>.Work("validation_failed");

        if (ttl <= TimeSpan.Zero)
            return ProviderResult<Credential>.Work("validation_failed", "ttl must be positive");

        // A TTL longer than the maximum is a refusal, not a validation error:
        // truncating it to an hour would be the same silent narrowing as issuing
        // a wider scope, and the caller would not know it happened.
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
            return ProviderResult<Credential>.Work("validation_failed");
        }

        // The refusal. Checked before any network call, so an out-of-scope
        // request never reaches GitHub and never yields a credential.
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

        var appId = await secrets.ResolveAsync(appIdRef, ct).ConfigureAwait(false);
        if (!appId.IsOk)
            return ProviderResult<Credential>.Fail(appId.Error!);

        var key = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (!key.IsOk)
            return ProviderResult<Credential>.Fail(key.Error!);

        var installation = await secrets.ResolveAsync(installationRef, ct).ConfigureAwait(false);
        if (!installation.IsOk)
            return ProviderResult<Credential>.Fail(installation.Error!);

        string jwt;
        try
        {
            jwt = GitHubAppJwt.Mint(appId.Value!.Value, key.Value!.Value, clock());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return ProviderResult<Credential>.Infrastructure("key_invalid");
        }

        var token = await client.CreateInstallationTokenAsync(
            jwt,
            installation.Value!.Value.Trim(),
            new InstallationTokenRequest(
                Repositories: new[] { RepositoryName(requested.Repository) },
                Permissions: ToPermissionMap(requested.Permissions)),
            ct).ConfigureAwait(false);

        if (!token.IsOk)
            return ProviderResult<Credential>.Fail(token.Error!);

        var minted = token.Value!;
        var issuedAt = clock();

        // Register the token for scrubbing the moment it exists, before it can
        // reach any other code path (docs/01 4.8).
        scrubber.Register(minted.Token);

        var credential = new Credential(
            Id: $"gh-{Interlocked.Increment(ref issued)}-{issuedAt.ToUnixTimeSeconds()}",
            TargetSystem: TargetSystemName,
            Scope: requested.Text,
            Secret: minted.Token,
            IssuedAt: issuedAt,
            ExpiresAt: minted.ExpiresAt,
            Permissions: minted.Permissions,
            Actor: actor,
            Refreshable: true);

        return ProviderResult<Credential>.Ok(credential);
    }

    public async Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken ct)
    {
        if (credential is null)
            return ProviderResult<Credential>.Work("validation_failed");

        if (!credential.Refreshable)
            return ProviderResult<Credential>.Work("forbidden", "credential is not refreshable");

        if (!string.Equals(credential.TargetSystem, TargetSystemName, StringComparison.OrdinalIgnoreCase))
            return ProviderResult<Credential>.Work("validation_failed");

        // A refresh re-issues the same scope for the same actor. It goes through
        // the same scope check, so a credential minted under a grant that has
        // since been withdrawn is refused here rather than renewed.
        //
        // The TTL is rounded up to the next whole minute. A credential's Ttl is
        // measured from the remote's expires_at, which is truncated to the
        // second, so it is always a fraction short of the requested TTL; passing
        // it straight back would eventually round down to zero and be refused.
        var ttl = credential.Ttl <= TimeSpan.Zero
            ? MaxTtl
            : TimeSpan.FromMinutes(Math.Ceiling(credential.Ttl.TotalMinutes));

        return await IssueAsync(
            credential.Actor,
            credential.TargetSystem,
            credential.Scope,
            ttl > MaxTtl ? MaxTtl : ttl,
            ct).ConfigureAwait(false);
    }

    private static string RepositoryName(string ownerSlashName)
    {
        var slash = ownerSlashName.IndexOf('/');
        return slash < 0 ? ownerSlashName : ownerSlashName[(slash + 1)..];
    }

    private static Dictionary<string, string> ToPermissionMap(IReadOnlySet<string> permissions)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var permission in permissions)
        {
            var colon = permission.IndexOf(':');
            if (colon <= 0)
                continue;

            map[permission[..colon]] = permission[(colon + 1)..];
        }

        // GitHub always grants metadata:read on an installation token. Sending it
        // explicitly keeps the request and the response comparable.
        if (!map.ContainsKey("metadata"))
            map["metadata"] = "read";

        return map;
    }
}
