using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Contracts.Tests;

/// <summary>Version 1's local hand-off surface (RW18) against the session surface's contract.</summary>
public sealed class LocalSessionSurfaceContract : SessionSurfaceContract
{
    protected override ISessionSurfaceProvider Surface { get; } = new LocalSessionSurface();
}
