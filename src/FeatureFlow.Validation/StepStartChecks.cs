using FeatureFlow.Model;

namespace FeatureFlow.Validation;

/// <summary>
/// The step-start checks of docs/09 section 2 that need no provider: 31 (an action's evaluated inputs satisfy its schema,
/// <see cref="ActionDescriptor.InputErrors"/>; literals were checked at load), which the core applies to every action before
/// it starts (<c>RunCore.StepSpec</c>, <c>RunCore.ChildSpec</c>; LOG AFb-1) and an engine action's activity applies again to
/// the call it is given, and 33 (every <c>tier_tags</c> tag matches a tier of the manifest read at this step). Check 32
/// (consumed artifacts exist) is the workflow's, which knows the run's artifacts.
/// </summary>
public static class StepStartChecks
{
    /// <summary>Check 31: what is wrong with an action's evaluated inputs, as messages; empty when they satisfy its schema.</summary>
    public static IReadOnlyList<string> ActionInputs(string action, IReadOnlyDictionary<string, object?> inputs) =>
        Actions.Get(action)?.InputErrors(inputs) ?? [];

    /// <summary>Check 33: a diagnostic for each tag that matches no tier in the manifest read at this step.</summary>
    public static void TierTags(string repository, RepositoryManifest manifest, IEnumerable<string> tags, DiagnosticBag d)
    {
        foreach (var tag in manifest.UnknownTags(tags))
            d.Add(Checks.TierTags, $"{repository}: tag '{tag}' matches no tier of {RepositoryManifest.FileName} (its tiers: {string.Join(", ", manifest.Tiers.Select(t => $"{t.Name} [{string.Join(", ", t.Tags)}]"))})");
    }
}
