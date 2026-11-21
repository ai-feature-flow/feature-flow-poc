namespace FeatureFlow.Providers.Abstractions;

/// <summary>Revisions as people read them.</summary>
public static class Revisions
{
    /// <summary>The first ten characters of a revision, for events, traces and messages.</summary>
    public static string Short(string revision) => revision.Length > 10 ? revision[..10] : revision;
}

/// <summary>The references the engine keeps in a repository beside its branches (docs/04 section 3.7, LOG 96).</summary>
public static class WorkRefs
{
    /// <summary>An agent step's hidden reference: the commits of its latest attempt, kept between attempts.</summary>
    public static string Hidden(string runId, string stepId) => $"refs/feature-flow/wip/{Safe(runId)}/{Safe(stepId)}";

    /// <summary>
    /// Where commits a reset or a later attempt discards are kept: by run, step and the first twelve characters of the
    /// discarded revision (LOG 96).
    /// </summary>
    public static string Superseded(string runId, string stepId, string revision) =>
        $"refs/feature-flow/superseded/{Safe(runId)}/{Safe(stepId)}/{revision[..Math.Min(12, revision.Length)]}";

    /// <summary>A run or step id as a reference path segment: anything but letters, digits, <c>-</c>, <c>_</c> and <c>.</c> becomes <c>_</c>.</summary>
    public static string Safe(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
