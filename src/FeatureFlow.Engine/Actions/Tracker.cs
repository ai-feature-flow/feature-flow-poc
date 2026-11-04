using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// <c>tracker.update</c> (docs/04 section 12): one tracker operation with the engine's identity. Agents have no tracker
/// write identity (F5); this action and declarative <c>tracker</c> updates on steps are the write paths.
/// </summary>
static class Tracker
{
    /// <summary>
    /// <c>op</c> is <c>create</c> (<c>title</c>, <c>body</c>, <c>labels</c>, <c>parent</c>, <c>fields</c>, <c>project</c>),
    /// <c>update</c> (the same fields; those not given are left), <c>transition</c> (<c>status</c>, a library status name),
    /// <c>link</c> (<c>to</c>, <c>link_type</c>, default <c>relates</c>) or <c>comment</c> (<c>body</c>). <c>issue</c> defaults to
    /// the run's tracker issue. A create and a comment carry the step's key, so a retry adopts what the first made. An issue
    /// that does not exist is <c>not_found</c>; anything else the tracker refuses stops the step for a person. Output
    /// <c>issue</c> is the issue acted on (for a create, the new one).
    /// </summary>
    public static async Task<ActionResult> UpdateAsync(ActionRun run)
    {
        var tracker = run.Provider<ITrackerProvider>("tracker");
        var op = run.String("op") ?? throw ActionStop.CannotAnswer("tracker.update needs op: create, update, transition, link or comment");
        IssueRef Issue() => new(run.String("issue") ?? run.Scope.TrackerIssue
            ?? throw ActionStop.CannotAnswer($"tracker.update {op} names no issue and the run has no tracker issue"));
        string Required(string name) => run.String(name) ?? throw ActionStop.CannotAnswer($"tracker.update {op} needs {name}");

        ProviderResult<IssueRef> result;
        switch (op)
        {
            case "create":
                result = await tracker.CreateAsync(Spec(run) with { Title = Required("title") }, run.Key, run.Ct);
                break;
            case "update":
                var issue = Issue();
                result = await tracker.UpdateAsync(issue, Spec(run), run.Ct);
                break;
            case "transition":
                issue = Issue();
                result = Map(await tracker.TransitionAsync(issue, Required("status"), run.Ct), issue);
                break;
            case "link":
                issue = Issue();
                result = Map(await tracker.LinkAsync(issue, new IssueRef(Required("to")), run.String("link_type") ?? "relates", run.Ct), issue);
                break;
            case "comment":
                issue = Issue();
                result = Map(await tracker.CommentAsync(issue, Required("body"), run.Key, run.Ct), issue);
                break;
            default:
                throw ActionStop.CannotAnswer($"tracker.update has no op '{op}'");
        }

        if (result is { Ok: false, Error.Reason: "not_found" })
        {
            run.Events.Add($"tracker.update {op}: {result.Error.Message}");
            return run.Report("not_found");
        }

        if (result is { Ok: false, Error.Class: ProviderErrorClass.Work } refused)
            throw ActionStop.CannotAnswer($"the tracker refused {op}: {refused.Error!.Reason}: {refused.Error.Message}");
        var done = ActionRun.Must(result, $"tracker.update {op}");
        run.Events.Add($"tracker.update {op} on {done.Key}");
        return run.Report("done", new Dictionary<string, object?> { ["issue"] = done.Key });
    }

    static IssueSpec Spec(ActionRun run) =>
        new(run.String("title"), run.String("body"), run.Inputs.ContainsKey("labels") ? run.Strings("labels") : null,
            run.String("parent") is { } parent ? new IssueRef(parent) : null,
            run.Inputs.GetValueOrDefault("fields") is IDictionary<string, object?> fields
                ? fields.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal)
                : null,
            run.String("project"));

    static ProviderResult<IssueRef> Map<T>(ProviderResult<T> result, IssueRef issue) =>
        result.Ok ? ProviderResult<IssueRef>.Success(issue) : new ProviderResult<IssueRef>(null, result.Error);
}
