using System.Globalization;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

// Spike 2 verification harness: proves the secrets provider and the credential
// broker against the real GitHub API. Check R6 is "the agent's credentials are
// short-lived, scoped to one repository, and expire", so the harness prints the
// real expiry and permission set and withholds the token value throughout.

var failures = new List<string>();

void Check(string label, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail is null ? "" : $" -- {detail}")}");
    if (!ok)
        failures.Add(label);
}

Console.WriteLine("== Spike 2: secrets provider and credential broker ==");
Console.WriteLine();

var scrubber = new SecretScrubber();
var secrets = new LocalFileSecretsProvider(scrubber: scrubber);

// ---------------------------------------------------------------- 1. secrets
Console.WriteLine("1. Secrets provider (by reference, floor F2)");

var secretsCheck = await secrets.CheckAsync(CancellationToken.None);
Check("secrets provider reachable", secretsCheck.IsOk,
    secretsCheck.IsOk ? secretsCheck.Value!.Detail : secretsCheck.Error!.Reason);

var appIdSecret = await secrets.ResolveAsync(
    SecretReference.Parse("secrets/github-app-id"), CancellationToken.None);
Check("resolve secrets/github-app-id", appIdSecret.IsOk,
    appIdSecret.IsOk ? $"{appIdSecret.Value!.Describe()}" : appIdSecret.Error!.Reason);

var keySecret = await secrets.ResolveAsync(
    SecretReference.Parse("secrets/github-app-key"), CancellationToken.None);
Check("resolve secrets/github-app-key", keySecret.IsOk,
    keySecret.IsOk ? keySecret.Value!.Describe() : keySecret.Error!.Reason);

var missing = await secrets.ResolveAsync(
    SecretReference.Parse("secrets/does-not-exist"), CancellationToken.None);
Check("unknown key returns not_found", !missing.IsOk && missing.Error!.Reason == "not_found",
    missing.Error?.Reason);

if (!appIdSecret.IsOk || !keySecret.IsOk)
{
    Console.WriteLine("\nCannot continue: the App id or key did not resolve.");
    return 1;
}

var appId = appIdSecret.Value!.Value.Trim();
var privateKey = keySecret.Value!.Value;

Console.WriteLine($"  secrets file: {secrets.Path}");
Console.WriteLine($"  key material: {keySecret.Value!.Describe()}");
Console.WriteLine();

// ------------------------------------------------------------- 2. JWT / /app
Console.WriteLine("2. App JWT accepted by GitHub (GET /app)");

var client = new GitHubAppClient();
var jwt = GitHubAppJwt.Mint(appId, privateKey, DateTimeOffset.UtcNow);
Console.WriteLine($"  jwt: minted for app {appId} (RS256, 10 min max)");

var app = await client.GetAppAsync(jwt, CancellationToken.None);
Check("GET /app accepted the JWT", app.IsOk,
    app.IsOk ? null : $"{app.Error!.Reason} {app.Error.Detail}");

if (app.IsOk)
{
    Console.WriteLine($"  app name:  {app.Value!.Name}");
    Console.WriteLine($"  app id:    {app.Value.Id}");
    Console.WriteLine($"  owner:     {app.Value.Owner}");
    Console.WriteLine($"  html_url:  {app.Value.HtmlUrl}");
}

Console.WriteLine();

// --------------------------------------------------------- 3. issue in scope
Console.WriteLine("3. Issue a scoped credential (R6)");

var broker = new GitHubAppCredentialBroker(secrets, client, scrubber);
var brokerCheck = await broker.CheckAsync(CancellationToken.None);
Check("broker check reaches GitHub as the App", brokerCheck.IsOk,
    brokerCheck.IsOk ? brokerCheck.Value!.Detail : brokerCheck.Error!.Reason);

var actor = Actor.Agent("claude-code").ForStep("run-spike2-1", "implement");
var scope = "repo:ai-feature-flow/feature-flow-poc contents:write pull_requests:write";
var ttl = TimeSpan.FromMinutes(60);

var issued = await broker.IssueAsync(actor, "github", scope, ttl, CancellationToken.None);
Check("issue in-scope credential", issued.IsOk,
    issued.IsOk ? null : $"{issued.Error!.Reason} {issued.Error.Detail}");

if (!issued.IsOk)
{
    Console.WriteLine("\nCannot continue: no credential was issued.");
    return 1;
}

var credential = issued.Value!;
Console.WriteLine($"  credential id: {credential.Id}");
Console.WriteLine($"  target system: {credential.TargetSystem}");
Console.WriteLine($"  scope:         {credential.Scope}");
Console.WriteLine($"  issued_at:     {credential.IssuedAt:O}");
Console.WriteLine($"  expires_at:    {credential.ExpiresAt:O}   (from the remote, not computed)");
Console.WriteLine($"  ttl:           {credential.Ttl.TotalMinutes:F1} min");
Console.WriteLine($"  secret:        {credential.Secret.Length} chars, value withheld ({SecretScrubber.Fingerprint(credential.Secret)})");
Console.WriteLine($"  actor:         {credential.Actor}");
Console.WriteLine("  permissions:");
foreach (var (name, access) in credential.Permissions.OrderBy(p => p.Key, StringComparer.Ordinal))
    Console.WriteLine($"    {name}: {access}");

Console.WriteLine();

// ------------------------------------------------------------ 4. TTL and expiry
Console.WriteLine("4. TTL is about 60 minutes, and expiry is assertable");

var ttlMinutes = credential.Ttl.TotalMinutes;
Check("ttl is about 60 minutes", ttlMinutes is >= 55 and <= 65, $"{ttlMinutes:F1} min");
Check("not expired now", !credential.IsExpired(credential.IssuedAt.AddMinutes(1)));
Check("expired one minute past expiry", credential.IsExpired(credential.ExpiresAt.AddMinutes(1)));
Check("expired one hour past expiry", credential.IsExpired(credential.ExpiresAt.AddHours(1)));
Check("remaining is positive before expiry",
    credential.Remaining(credential.IssuedAt) > TimeSpan.Zero,
    credential.Remaining(credential.IssuedAt).ToString());

Console.WriteLine();

// --------------------------------------------------------- 5. scope refusal
Console.WriteLine("5. Out-of-scope requests are refused with forbidden");

// The refusal must happen before any network call. This broker is wired to a
// client that throws if it is used, so a PASS here proves no request was made.
var offlineBroker = new GitHubAppCredentialBroker(
    secrets, new RefusingGitHubAppClient(), scrubber);

var offline = await offlineBroker.IssueAsync(
    actor, "github", "repo:ai-feature-flow/some-other-repo contents:write", ttl, CancellationToken.None);

Check("refused without any network call",
    !offline.IsOk && offline.Error!.Class == ErrorClass.Work && offline.Error.Reason == "forbidden",
    offline.Error?.Detail);

Console.WriteLine();

// A scope the broker cannot grant. Refused before any network call, so GitHub
// never sees the request and no credential is minted.
var refusals = new (string Label, string Scope, TimeSpan Ttl)[]
{
    ("another repository", "repo:ai-feature-flow/some-other-repo contents:write", ttl),
    ("a permission not granted", "repo:ai-feature-flow/feature-flow-poc contents:write admin:write", ttl),
    ("a wider access level", "repo:ai-feature-flow/feature-flow-poc contents:admin", ttl),
    ("a ttl longer than the maximum", scope, TimeSpan.FromHours(8)),
};

foreach (var (label, refusalScope, refusalTtl) in refusals)
{
    var result = await broker.IssueAsync(actor, "github", refusalScope, refusalTtl, CancellationToken.None);

    var refused = !result.IsOk
                  && result.Error!.Class == ErrorClass.Work
                  && result.Error.Reason == "forbidden";

    Check($"refused: {label}", refused,
        refused ? result.Error!.Detail : $"class={result.Error?.Class} reason={result.Error?.Reason}");
}

// A malformed request is a different thing from an ungrantable one: the broker
// cannot tell what was asked for, so it says validation_failed rather than
// guessing at a scope and refusing it.
Console.WriteLine("   malformed requests (validation_failed, not forbidden):");

var malformed = new (string Label, string Target, string Scope, TimeSpan Ttl)[]
{
    ("no repository at all", "github", "contents:write", ttl),
    ("an empty scope", "github", "", ttl),
    ("a zero ttl", "github", scope, TimeSpan.Zero),
    ("an unknown target system", "gitlab", scope, ttl),
};

foreach (var (label, target, badScope, badTtl) in malformed)
{
    var result = await broker.IssueAsync(actor, target, badScope, badTtl, CancellationToken.None);

    var rejected = !result.IsOk
                   && result.Error!.Class == ErrorClass.Work
                   && result.Error.Reason == "validation_failed";

    Check($"rejected: {label}", rejected,
        rejected ? result.Error!.Detail : $"class={result.Error?.Class} reason={result.Error?.Reason}");
}

Console.WriteLine();

// ------------------------------------------------------------- 6. refresh
Console.WriteLine("6. Refresh re-issues the same scope");

var refreshed = await broker.RefreshAsync(credential, CancellationToken.None);
Check("refresh succeeds", refreshed.IsOk,
    refreshed.IsOk ? null : $"{refreshed.Error!.Reason} {refreshed.Error.Detail}");

if (refreshed.IsOk)
{
    var next = refreshed.Value!;
    Check("refreshed scope is unchanged", next.Scope == credential.Scope, next.Scope);
    Check("refreshed credential is a new issuance", next.Id != credential.Id, next.Id);
    Check("refreshed credential is not expired", !next.IsExpired(next.IssuedAt.AddMinutes(1)));
    Console.WriteLine($"  refreshed expires_at: {next.ExpiresAt:O}");
}

Console.WriteLine();

// ------------------------------------------------------------ 7. scrubbing
Console.WriteLine("7. Scrubbing redacts secrets from text (docs/01 4.8)");

var token = credential.Secret;

// The real token is checked but never printed, so the harness cannot itself
// leak the thing it is proving gets redacted. The visible before/after uses a
// stand-in value registered the same way.
var logLine = $"POST https://api.github.com/repos/x/y -> 201 (Authorization: Bearer {token})";
Check("scrubber detects the real token in a log line", scrubber.ContainsSecret(logLine));
Check("scrubbing removes the real token", !scrubber.Scrub(logLine).Contains(token, StringComparison.Ordinal));

const string standIn = "ghs_standInValueForScrubDemo0123456789";
scrubber.Register(standIn);

var demoLine = $"git push https://x-access-token:{standIn}@github.com/ai-feature-flow/feature-flow-poc.git";
var scrubbed = scrubber.Scrub(demoLine);

Console.WriteLine($"  before: {demoLine}");
Console.WriteLine($"  after:  {scrubbed}");

Check("scrubbed line no longer contains the value", !scrubbed.Contains(standIn, StringComparison.Ordinal));
Check("scrubbed line carries the redaction marker", scrubbed.Contains(SecretScrubber.Redaction, StringComparison.Ordinal));
Check("scrubber no longer detects a secret in the scrubbed line", !scrubber.ContainsSecret(scrubbed));

var pemLine = "key material follows: " + privateKey;
Check("scrubber detects a PEM by shape", scrubber.ContainsSecret(pemLine));
Check("scrubbed PEM is redacted", !scrubber.Scrub(pemLine).Contains("PRIVATE KEY", StringComparison.Ordinal));

var jwtLine = $"Authorization: Bearer {jwt}";
Check("scrubber detects a JWT by shape", scrubber.ContainsSecret(jwtLine));
Check("scrubbed JWT is redacted", !scrubber.Scrub(jwtLine).Contains(jwt, StringComparison.Ordinal));

var cleanLine = "step implement completed in 42s";
Check("ordinary text is left alone", scrubber.Scrub(cleanLine) == cleanLine);

Console.WriteLine();

// ------------------------------------------------- 8. no raw secret in records
Console.WriteLine("8. Nothing written to a record carries a raw secret");

// The expiry is written in UTC with a Z suffix, so it survives JSON escaping
// unchanged and can be read back and compared.
var expiresText = credential.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

var record = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["credential_id"] = credential.Id,
    ["target"] = credential.TargetSystem,
    ["scope"] = credential.Scope,
    ["expires_at"] = expiresText,
    ["secret_fingerprint"] = SecretScrubber.Fingerprint(credential.Secret),
    ["note"] = scrubbed,
};

var serialised = System.Text.Json.JsonSerializer.Serialize(record);
Console.WriteLine($"  record: {serialised}");

Check("record contains no raw token", !serialised.Contains(token, StringComparison.Ordinal));
Check("record contains no raw key", !serialised.Contains(privateKey, StringComparison.Ordinal));
Check("record still states the expiry", serialised.Contains(expiresText, StringComparison.Ordinal));

var roundTripped = System.Text.Json.JsonSerializer
    .Deserialize<Dictionary<string, string>>(serialised)!["expires_at"];
Check("expiry round-trips and is assertable",
    credential.IsExpired(DateTimeOffset.Parse(roundTripped, CultureInfo.InvariantCulture).AddMinutes(1)));

Console.WriteLine();

// ------------------------------------------------------------------- summary
Console.WriteLine("== Summary ==");
Console.WriteLine($"  credentials issued: {broker.IssuedCount}");
Console.WriteLine($"  secrets registered for scrubbing: {scrubber.Count}");
Console.WriteLine($"  checks failed: {failures.Count}");

foreach (var failure in failures)
    Console.WriteLine($"    - {failure}");

return failures.Count == 0 ? 0 : 1;

/// <summary>
/// A client that fails the harness if it is called. Used to prove that an
/// out-of-scope request is refused locally rather than sent to GitHub.
/// </summary>
internal sealed class RefusingGitHubAppClient : GitHubAppClient
{
    public override Task<ProviderResult<AppInfo>> GetAppAsync(string jwt, CancellationToken ct) =>
        throw new InvalidOperationException("the broker must not call GitHub for an out-of-scope request");

    public override Task<ProviderResult<InstallationToken>> CreateInstallationTokenAsync(
        string jwt, string installationId, InstallationTokenRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("the broker must not call GitHub for an out-of-scope request");
}
