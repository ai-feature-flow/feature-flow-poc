using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// docs/06 section 1.1: <c>unauthenticated</c> and <c>forbidden</c> are infrastructure errors, but almost always a
/// configuration fault (a revoked App, a missing permission, a wrong key), which retrying for ever would hide. They get
/// a short budget of tries, waiting <see cref="FirstDelay"/> and doubling (1, 2, 4 and 8 s by default), then the step or
/// run stops for a person with the fault named. Any other infrastructure error is still retried without limit.
/// </summary>
public static class ConfigurationFaults
{
    /// <summary>Tries before the budget is spent: the first and four retries.</summary>
    public const int Tries = 5;

    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    public static bool Is(string? reason) => reason is "unauthenticated" or "forbidden";

    public static bool Is(ProviderError? error) => Is(error?.Reason);

    /// <summary>What a person is told when the budget is spent.</summary>
    public static string Describe(string failure) =>
        $"a configuration fault: still refused after {Tries} tries ({failure}). Check the installation's credential broker: the GitHub App's key, " +
        "installation and permissions. Retry once it is fixed";

    /// <summary>
    /// Runs <paramref name="body"/>, again after a wait while it throws a configuration fault, and returns
    /// <paramref name="exhausted"/>'s value once the budget is spent. Other exceptions pass through.
    /// </summary>
    public static async Task<T> WithBudgetAsync<T>(Func<Task<T>> body, TimeSpan firstDelay, Action heartbeat, Func<string, T> exhausted, CancellationToken ct)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                return await body();
            }
            catch (ActionInfrastructureException e) when (Is(e.Reason))
            {
                if (tries >= Tries)
                    return exhausted(Describe(e.Message));
                await WaitAsync(firstDelay * (1 << (tries - 1)), heartbeat, ct);
            }
        }
    }

    /// <summary>A provider read with the same budget: the last result, a configuration fault still, once it is spent.</summary>
    public static async Task<ProviderResult<T>> ReadAsync<T>(Func<Task<ProviderResult<T>>> read, TimeSpan firstDelay, Action heartbeat, CancellationToken ct)
    {
        for (var tries = 1; ; tries++)
        {
            var result = await read();
            if (!Is(result.Error) || tries >= Tries)
                return result;
            await WaitAsync(firstDelay * (1 << (tries - 1)), heartbeat, ct);
        }
    }

    static async Task WaitAsync(TimeSpan delay, Action heartbeat, CancellationToken ct)
    {
        heartbeat();
        await Task.Delay(delay, ct);
        heartbeat();
    }
}
