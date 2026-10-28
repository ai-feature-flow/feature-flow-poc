using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// The base contract every provider implements. docs/04 section 1.1.
/// </summary>
public interface IProvider
{
    ProviderDescriptor Describe();

    Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct);
}

/// <summary>
/// What <see cref="IProvider.Describe"/> returns. docs/04 section 1.1.
/// </summary>
public sealed record ProviderDescriptor(
    string Name,
    string Version,
    IReadOnlyList<object> Capabilities,
    IReadOnlyList<string> Requires,
    string ConfigSchemaRef);

/// <summary>
/// The result of a provider health check. docs/04 section 1.1.
/// </summary>
public sealed record CheckResult(
    bool Ok,
    string? Detail = null,
    TimeSpan? Latency = null);

/// <summary>
/// The empty payload, for provider methods that return no value.
/// </summary>
public sealed record Unit
{
    public static Unit Value { get; } = new();
}
