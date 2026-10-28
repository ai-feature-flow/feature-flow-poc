using System.Text.Json;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// A secrets provider backed by a JSON file that lives outside the repository.
/// docs/07 section 8 and floor F2: configuration holds references, never values.
///
/// Spike scope. The point is the reference/value split and the scrubbing
/// contract, not the storage technology. A real deployment uses a vault; the
/// interface is the same.
/// </summary>
public sealed class LocalFileSecretsProvider : ISecretsProvider
{
    /// <summary>Where the secrets file lives when FF_SECRETS_FILE is not set.</summary>
    public const string DefaultSecretsFile = @"D:\Users\Stuart\keys\feature-flow-secrets.json";

    /// <summary>docs/05 section 10: ResolveAsync timeout is 15s.</summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(15);

    private readonly string path;
    private readonly SecretScrubber scrubber;
    private readonly Func<DateTimeOffset> clock;

    private Dictionary<string, string>? cache;
    private DateTimeOffset loadedAt;

    public LocalFileSecretsProvider(
        string? path = null,
        SecretScrubber? scrubber = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.path = path
                    ?? Environment.GetEnvironmentVariable("FF_SECRETS_FILE")
                    ?? DefaultSecretsFile;
        this.scrubber = scrubber ?? new SecretScrubber();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The file this provider reads. Safe to log; it is a path, not a value.</summary>
    public string Path => path;

    public ProviderDescriptor Describe() => new(
        Name: "local-file",
        Version: "0.1.0",
        Capabilities: new object[] { new SecretsCapabilities(ReadFileValues: true, OutsideRepositoryOnly: true) },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "feature-flow/schemas/secrets/local-file/v1");

    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();
        try
        {
            var values = await LoadAsync(ct).ConfigureAwait(false);
            return ProviderResult<CheckResult>.Ok(new CheckResult(
                Ok: true,
                Detail: $"{values.Count} keys from {path}",
                Latency: clock() - started));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult<CheckResult>.Infrastructure("secrets_unreadable");
        }
    }

    public async Task<ProviderResult<SecretValue>> ResolveAsync(SecretReference reference, CancellationToken ct)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Key))
            return ProviderResult<SecretValue>.Work("validation_failed");

        Dictionary<string, string> values;
        try
        {
            values = await LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult<SecretValue>.Infrastructure("secrets_unreadable");
        }

        if (!values.TryGetValue(reference.Key, out var raw))
            return ProviderResult<SecretValue>.Work("not_found");

        var value = Materialise(raw);

        // Every resolved value is registered for scrubbing, so a later log line
        // or record-store entry that quotes it is redacted (docs/01 4.8).
        scrubber.Register(value.Value);

        return ProviderResult<SecretValue>.Ok(value);
    }

    /// <summary>
    /// A value that names an existing absolute file is the file's content, so a
    /// key reference can yield the PEM text without inlining the key.
    /// </summary>
    private static SecretValue Materialise(string raw)
    {
        if (LooksLikeExistingFile(raw, out var full))
        {
            try
            {
                var content = File.ReadAllText(full);
                return new SecretValue(content, SecretValueKind.FileContent, full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through: treat it as an inline value rather than failing
                // closed on a value we cannot read.
            }
        }

        return new SecretValue(raw, SecretValueKind.Inline);
    }

    private static bool LooksLikeExistingFile(string raw, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096)
            return false;

        // A PEM or a multi-line value is never a path.
        if (raw.Contains('\n') || raw.Contains('\r'))
            return false;

        if (!System.IO.Path.IsPathRooted(raw))
            return false;

        try
        {
            full = System.IO.Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return File.Exists(full);
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (cache is not null)
            return cache;

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var values = parsed ?? new Dictionary<string, string>(StringComparer.Ordinal);
        cache = new Dictionary<string, string>(values, StringComparer.Ordinal);
        loadedAt = clock();
        return cache;
    }

    /// <summary>When the file was read. Used by the check result.</summary>
    public DateTimeOffset? LoadedAt => cache is null ? null : loadedAt;
}
