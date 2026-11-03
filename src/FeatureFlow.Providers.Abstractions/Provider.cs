using FeatureFlow.Model;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>The error classes of docs/06 section 1.1.</summary>
public enum ProviderErrorClass
{
    Infrastructure,
    Work,
}

/// <summary>An expected failure: a reason from docs/06 section 1.1 and a message. Exceptions are programming errors.</summary>
public sealed record ProviderError(string Reason, string Message, TimeSpan? RetryAfter = null)
{
    static readonly HashSet<string> InfrastructureReasons =
        ["unreachable", "server_error", "internal_error", "timeout", "rate_limited", "unauthenticated", "forbidden"];

    public ProviderErrorClass Class => InfrastructureReasons.Contains(Reason) ? ProviderErrorClass.Infrastructure : ProviderErrorClass.Work;

    /// <summary>
    /// docs/06 section 1.1: <c>unauthenticated</c> and <c>forbidden</c> are almost always a configuration fault (a revoked
    /// App, a missing permission, a wrong key), which retrying for ever would hide. A provider retries once, after
    /// <see cref="FreshCredentialDelay"/>, with a freshly issued credential; what still reaches the engine stops for a person (LOG 147).
    /// </summary>
    public bool IsConfigurationFault => Reason is "unauthenticated" or "forbidden";

    /// <summary>How long a provider waits before its one fresh-credential retry: a newly minted token can take a moment to be accepted.</summary>
    public static readonly TimeSpan FreshCredentialDelay = TimeSpan.FromSeconds(2);

    /// <summary>What a person is told about a configuration fault.</summary>
    public string DescribeFault() =>
        $"a configuration fault ({Reason}: {Message}). Check the installation's credential broker: the GitHub App's key, installation and permissions. Retry once it is fixed";
}

public readonly record struct Unit;

/// <summary>A provider method's result: a value, or a classified error (docs/06 section 1).</summary>
public sealed record ProviderResult<T>(T? Value, ProviderError? Error)
{
    public bool Ok => Error is null;

    public static ProviderResult<T> Success(T value) => new(value, null);

    public static ProviderResult<T> Failure(string reason, string message) => new(default, new ProviderError(reason, message));

    public T Unwrap() => Error is null ? Value! : throw new ProviderException(Error);
}

/// <summary>Thrown by <see cref="ProviderResult{T}.Unwrap"/>; carries the classified error to the caller's classification.</summary>
public sealed class ProviderException(ProviderError error) : Exception($"{error.Reason}: {error.Message}")
{
    public ProviderError Error { get; } = error;
}

/// <summary>
/// What a provider says about itself (docs/06 section 1): its type, name and version, and the capabilities it
/// offers as names <c>&lt;type&gt;.&lt;flag&gt;</c> derived from its typed capability record.
/// </summary>
public sealed record ProviderDescription(string Type, string Name, string Version, object Capabilities)
{
    /// <summary>The capability names: the type itself, and <c>type.flag</c> for every true flag of the record.</summary>
    public IEnumerable<string> CapabilityNames()
    {
        yield return Type;
        foreach (var property in Capabilities.GetType().GetProperties())
            if (property.PropertyType == typeof(bool) && (bool)property.GetValue(Capabilities)!)
                yield return $"{Type}.{SnakeCase(property.Name)}";
    }

    static string SnakeCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>The base interface of every provider (docs/06 section 1).</summary>
public interface IProvider
{
    ProviderDescription Describe();

    /// <summary>Called at startup and at run start: is the provider usable?</summary>
    Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a factory receives: the instance's settings, the configuration file's folder, and the default
/// instance of another provider type, for a provider that needs one (a runtime writing artifacts).
/// </summary>
public sealed record ProviderContext(ProviderInstanceConfig Instance, string BaseDirectory, Func<string, IProvider?> Provider)
{
    public string? Setting(string key) => Instance.Settings[key] is Model.Yaml.YScalar { IsNull: false } s ? s.Text : null;

    public string RequiredSetting(string key) =>
        Setting(key) ?? throw new InvalidOperationException($"providers.{Instance.Type}.instances.{Instance.Name} needs '{key}'");

    /// <summary>A path setting, relative to the configuration file's folder.</summary>
    public string? PathSetting(string key) => Setting(key) is { } p ? Path.GetFullPath(Path.Combine(BaseDirectory, p)) : null;
}

/// <summary>Creates one kind of provider for one type, by the kind name configuration uses (docs/06 section 4).</summary>
public sealed record ProviderFactory(string Type, string Kind, IReadOnlyList<string> Settings, Func<ProviderContext, IProvider> Create);
