using FeatureFlow.Model;

namespace FeatureFlow.Validation;

/// <summary>
/// The step-start checks of docs/09 section 2 that need no provider and no run state: 33 (every <c>tier_tags</c> tag matches
/// a tier of the manifest read at this step). Check 31 (an action's evaluated inputs satisfy its schema) is the core's, which
/// evaluates them and applies it to every action before it starts (<c>RunCore.StepSpec</c>, <c>RunCore.ChildSpec</c>; LOG
/// 488); check 32 (consumed artifacts exist) is the core's too, which knows the run's artifacts.
/// </summary>
public static class StepStartChecks
{
    /// <summary>Check 33: a diagnostic for each tag that matches no tier in the manifest read at this step.</summary>
    public static void TierTags(string repository, RepositoryManifest manifest, IEnumerable<string> tags, DiagnosticBag d)
    {
        foreach (var tag in manifest.UnknownTags(tags))
            d.Add(Checks.TierTags, $"{repository}: tag '{tag}' matches no tier of {RepositoryManifest.FileName} (its tiers: {string.Join(", ", manifest.Tiers.Select(t => $"{t.Name} [{string.Join(", ", t.Tags)}]"))})");
    }
}
