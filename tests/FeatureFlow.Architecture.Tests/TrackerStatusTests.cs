using System.Text.RegularExpressions;

namespace FeatureFlow.Architecture.Tests;

/// <summary>
/// L8 (docs/06 section 3.6): a tracker provider knows no library's statuses. A status its configuration's <c>status_map</c>
/// does not name is the tracker status of the same name, so std's status names (<c>library/std/status_map.yaml</c>) appear
/// as a string in no tracker provider's source: its own files and the factories that make it.
/// </summary>
public partial class TrackerStatusTests
{
    [GeneratedRegex(@"^\s*-\s*name:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex StatusName();

    [Fact]
    public void No_tracker_provider_names_std_s_statuses()
    {
        var statuses = StatusName().Matches(File.ReadAllText(Path.Combine(Repository.Root, "library", "std", "status_map.yaml")))
            .Select(m => m.Groups[1].Value).ToList();
        var trackers = Directory.EnumerateFiles(Path.Combine(Repository.Root, "src", "Providers"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: Path.GetRelativePath(Repository.Root, path), Text: File.ReadAllText(path)))
            .Where(file => file.Text.Contains("ITrackerProvider", StringComparison.Ordinal) || file.Text.Contains("new(\"tracker\"", StringComparison.Ordinal))
            .ToList();

        var offending = trackers.SelectMany(file => statuses.Where(s => file.Text.Contains($"\"{s}\"", StringComparison.Ordinal)).Select(s => $"{file.Path}: \"{s}\"")).ToList();

        Assert.Equal(["code_review", "done", "in_progress"], statuses.Order(StringComparer.Ordinal));
        Assert.True(trackers.Count >= 3, string.Join(", ", trackers.Select(t => t.Path)));
        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }
}
