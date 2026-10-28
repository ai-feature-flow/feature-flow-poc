using System.Globalization;

namespace FeatureFlow.Spike.Ci;

/// <summary>
/// One tier's CI binding. docs/07 section 5: <c>ci.mapping</c> selects a named
/// provider instance and a workflow per tier, which is how GitHub Actions and
/// TeamCity coexist without special cases in code.
/// </summary>
/// <param name="Tier">The test tier, e.g. <c>integration</c>.</param>
/// <param name="Instance">The named provider instance, e.g. <c>github-actions</c>.</param>
/// <param name="Workflow">The workflow file, e.g. <c>integration.yml</c>.</param>
/// <param name="Ref">The git ref to dispatch on. Defaults to the repository default branch.</param>
/// <param name="QueueBuilds">Whether to report results for <c>merge_group</c> runs of this workflow.</param>
public sealed record CiTierMapping(
    string Tier,
    string Instance,
    string Workflow,
    string? Ref = null,
    bool QueueBuilds = true)
{
    /// <summary>
    /// The workflow file as the dispatch endpoint wants it: a bare
    /// <c>integration.yml</c> or a full <c>.github/workflows/integration.yml</c>
    /// both work, so this normalises to the bare form.
    /// </summary>
    public string WorkflowFile
    {
        get
        {
            var file = Workflow.Replace('\\', '/');
            var lastSlash = file.LastIndexOf('/');
            return lastSlash >= 0 ? file[(lastSlash + 1)..] : file;
        }
    }

    public override string ToString() =>
        $"{Tier} -> {Instance} ({WorkflowFile})";
}

/// <summary>
/// The tier-to-workflow map. Configuration, not code (docs/07 section 5): the
/// provider is handed the map and never parses <c>feature-flow.yaml</c> itself.
/// </summary>
public sealed class CiTierMap
{
    private readonly Dictionary<string, CiTierMapping> byTier;

    public CiTierMap(IEnumerable<CiTierMapping> mappings)
    {
        byTier = new Dictionary<string, CiTierMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings ?? throw new ArgumentNullException(nameof(mappings)))
            byTier[mapping.Tier] = mapping;
    }

    /// <summary>The tiers this map knows, in the order they were given.</summary>
    public IReadOnlyList<string> Tiers => byTier.Values.Select(m => m.Tier).ToList();

    public bool TryGet(string tier, out CiTierMapping mapping) =>
        byTier.TryGetValue(tier, out mapping!);

    /// <summary>
    /// The mapping for a tier, or null. An unknown tier is
    /// <c>validation_failed</c> with class <see cref="ErrorClass.Work"/>, never
    /// infrastructure: the caller asked for a tier this repository does not
    /// declare (docs/05 section 5).
    /// </summary>
    public CiTierMapping? Get(string tier) =>
        byTier.TryGetValue(tier, out var mapping) ? mapping : null;

    /// <summary>
    /// The map for the Spike 2 fixture repository, read from its
    /// <c>feature-flow.yaml</c>. Kept here so the harness has a real default and
    /// so the shape is visible in one place.
    /// </summary>
    public static CiTierMap ForFeatureFlowExample(string? gitRef = "main") => new(new[]
    {
        new CiTierMapping("unit", "github-actions", "unit.yml", gitRef),
        new CiTierMapping("integration", "github-actions", "integration.yml", gitRef),
    });

    public override string ToString() => string.Join(
        ", ",
        byTier.Values.Select(m => m.ToString()).OrderBy(t => t, StringComparer.Ordinal));
}

/// <summary>
/// The inputs a dispatched workflow receives. The fixture's workflows declare
/// <c>commit_sha</c> and <c>idempotency_key</c>; a workflow that declares
/// neither still runs, because unknown inputs are simply ignored by the caller
/// and the workflow falls back to <c>github.sha</c>.
/// </summary>
public static class WorkflowInputs
{
    public const string CommitSha = "commit_sha";
    public const string IdempotencyKey = "idempotency_key";

    public static IReadOnlyDictionary<string, string> For(string commitSha, string idempotencyKey) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CommitSha] = commitSha,
            [IdempotencyKey] = idempotencyKey,
        };
}

/// <summary>
/// The GitHub Actions event names the provider treats as first-class.
/// docs/04 section 5: the provider must report results for queue builds, so
/// <c>merge_group</c> is a named constant, not a string literal buried in a
/// filter.
/// </summary>
public static class GitHubActionsEvent
{
    /// <summary>A run started by the engine through <c>workflow_dispatch</c>.</summary>
    public const string WorkflowDispatch = "workflow_dispatch";

    /// <summary>A run started by a push.</summary>
    public const string Push = "push";

    /// <summary>
    /// A merge queue build. The merge queue path depends on the provider
    /// reporting these, so they are discovered and reported like any other run.
    /// </summary>
    public const string MergeGroup = "merge_group";
}
