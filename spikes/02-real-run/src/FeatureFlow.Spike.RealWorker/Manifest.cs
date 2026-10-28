using FeatureFlow.Spike.CodeHost;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Reads the facts a step needs from <c>feature-flow.yaml</c>.
///
/// <para>
/// docs/07 5.1.2: tiers and their execution targets are repository facts, read
/// per step. A plan that listed them would copy repository facts into the plan
/// and go stale — the exact failure review 04 Q19 removed.
/// </para>
///
/// <para>
/// Spike scope: a line-oriented reader over the manifest's own shape, not a YAML
/// parser. The manifest is small and its shape is fixed by the fixture; what
/// matters is that the values come from the branch, per step, and that the hash
/// is recorded (R16).
/// </para>
/// </summary>
public static class Manifest
{
    /// <summary>The execution target a tier declares: "sandbox" or "ci".</summary>
    public static string? TierTarget(FileContent? manifest, string tier)
    {
        if (manifest is null)
            return null;

        var lines = manifest.Content.Split('\n');
        var inTiers = false;
        var inThisTier = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("testTiers:", StringComparison.Ordinal))
            {
                inTiers = true;
                continue;
            }

            if (inTiers && line.Length > 0 && !line.StartsWith(" ", StringComparison.Ordinal) &&
                !line.StartsWith("-", StringComparison.Ordinal))
            {
                // A new top-level key ends the tier block.
                inTiers = false;
                inThisTier = false;
                continue;
            }

            if (!inTiers)
                continue;

            if (line.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            {
                var name = ValueAfter(line, "- name:");
                inThisTier = string.Equals(name, tier, StringComparison.Ordinal);
                continue;
            }

            if (inThisTier && line.TrimStart().StartsWith("target:", StringComparison.Ordinal))
            {
                return ValueAfter(line, "target:");
            }
        }

        return null;
    }

    /// <summary>The commands a sandbox tier runs.</summary>
    public static List<string> TierCommands(FileContent? manifest, string tier)
    {
        var commands = new List<string>();
        if (manifest is null)
            return commands;

        var lines = manifest.Content.Split('\n');
        var inTiers = false;
        var inThisTier = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("testTiers:", StringComparison.Ordinal))
            {
                inTiers = true;
                continue;
            }

            if (inTiers && line.Length > 0 && !line.StartsWith(" ", StringComparison.Ordinal) &&
                !line.StartsWith("-", StringComparison.Ordinal))
            {
                inTiers = false;
                inThisTier = false;
                continue;
            }

            if (!inTiers)
                continue;

            if (line.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            {
                var name = ValueAfter(line, "- name:");
                inThisTier = string.Equals(name, tier, StringComparison.Ordinal);
                continue;
            }

            if (inThisTier && line.TrimStart().StartsWith("commands:", StringComparison.Ordinal))
            {
                var inline = ValueAfter(line, "commands:");
                if (inline.StartsWith("[", StringComparison.Ordinal))
                {
                    var inner = inline.Trim('[', ']');
                    foreach (var part in inner.Split(','))
                    {
                        var command = part.Trim().Trim('"', '\'');
                        if (command.Length > 0)
                            commands.Add(command);
                    }
                }
            }
        }

        return commands;
    }

    /// <summary>
    /// The repository's preferred merge method. docs/07 section 5: the repository
    /// states it, and the step reads it per step. No merge method in a plan
    /// (review 09 C2).
    /// </summary>
    public static string? MergeMethod(FileContent? manifest)
    {
        if (manifest is null)
            return null;

        var lines = manifest.Content.Split('\n');
        var inMerge = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("merge:", StringComparison.Ordinal))
            {
                inMerge = true;
                continue;
            }

            if (inMerge && line.TrimStart().StartsWith("method:", StringComparison.Ordinal))
                return ValueAfter(line, "method:");

            if (inMerge && line.Length > 0 && !line.StartsWith(" ", StringComparison.Ordinal))
                inMerge = false;
        }

        return null;
    }

    /// <summary>The tiers the manifest declares, in order.</summary>
    public static List<string> Tiers(FileContent? manifest)
    {
        var tiers = new List<string>();
        if (manifest is null)
            return tiers;

        var inTiers = false;
        foreach (var raw in manifest.Content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("testTiers:", StringComparison.Ordinal))
            {
                inTiers = true;
                continue;
            }

            if (inTiers && line.Length > 0 && !line.StartsWith(" ", StringComparison.Ordinal) &&
                !line.StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            if (inTiers && line.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
                tiers.Add(ValueAfter(line, "- name:"));
        }

        return tiers;
    }

    /// <summary>
    /// The value after a key, from a possibly-indented line. The line keeps its
    /// leading whitespace, so the slice must be taken from the key's position in
    /// the trimmed text, not from a fixed offset.
    /// </summary>
    private static string ValueAfter(string line, string key)
    {
        var trimmed = line.TrimStart();
        return trimmed[key.Length..].Trim();
    }
}
