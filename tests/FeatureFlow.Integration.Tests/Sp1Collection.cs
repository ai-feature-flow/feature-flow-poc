namespace FeatureFlow.Integration.Tests;

/// <summary>A collection that runs with no other test in parallel: SP1's timing-sensitive scenario.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Sp1Collection
{
    public const string Name = "sp1 alone";
}
