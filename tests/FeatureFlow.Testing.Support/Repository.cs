namespace FeatureFlow.Testing.Support;

/// <summary>The repository the tests run from.</summary>
public static class Repository
{
    /// <summary>The folder holding <c>FeatureFlow.slnx</c>, found by walking up from the test's output folder.</summary>
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found above " + AppContext.BaseDirectory);
    }
}
