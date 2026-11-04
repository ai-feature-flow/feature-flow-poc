using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// An artifact the run holds now: the step and name it was produced under, its declared type, its reference, the
/// engine action that produced it (null for an agent step or decision), and whether its type is marked
/// <c>evidence: true</c> (F7: only engine actions may produce such types, check 12).
/// </summary>
public sealed record ArtifactEntry(string StepId, string Name, string? Type, string Ref, string? Action = null, bool Evidence = false);

/// <summary>
/// What an engine action knows of its run beyond its inputs, as the workflow computed it when the step started: the
/// run's repositories and the step's own, the work branch, a child run's parent branch (null: the repository's
/// default branch, read from the code host), the artifacts the run holds (evidence for <c>scm.merge</c>, the baseline
/// for <c>analysis.run</c>), the pull requests its <c>scm.pull_request</c> steps opened, F1's patterns as fixed at
/// run start (docs/05 section 8), and the artifacts the step consumes, by the name the step sees them under
/// (<c>requirements</c>, <c>parent/requirements</c>, <c>child/ticket_a/summary</c>), resolved as the workflow resolves an agent
/// step's; null where the workflow does not give them, and then a subject from this run is its latest of the name; the run's tracker issue
/// (<c>run.tracker_issue</c>), if it has one; and the keys of every artifact the step declares it consumes, resolved or not
/// (<see cref="Declared"/>), so an action can tell a declared optional artifact that does not exist from one the step never
/// consumes (LOG 191, review F1).
/// </summary>
public sealed record ActionScope(
    IReadOnlyList<string> Repositories,
    string? Repository,
    string WorkBranch,
    string? ParentBranch,
    IReadOnlyList<ArtifactEntry> Artifacts,
    IReadOnlyList<string> PullRequests,
    IReadOnlyList<string> EngineMayMerge,
    IReadOnlyDictionary<string, string>? Consumed = null,
    string? TrackerIssue = null,
    IReadOnlyList<string>? Declared = null)
{
    public static readonly ActionScope None = new([], null, "", null, [], [], []);
}

/// <summary>
/// A repository as a run names it (LOG 190, 198): a name an agent wrote (<c>api</c>, <c>Acme/API</c>) means the one of the
/// run's repositories it equals ignoring case, or that it is the last part of. A name that matches none, or several
/// (<c>api</c> against <c>acme/api</c> and <c>other/api</c>), is kept as written.
/// </summary>
public static class RepositoryNames
{
    public static string As(string written, IReadOnlyList<string> known)
    {
        var matches = known.Where(r => string.Equals(r, written, StringComparison.OrdinalIgnoreCase)
                                       || r.EndsWith("/" + written, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : written;
    }
}

/// <summary>
/// How an action ended. <see cref="Wait"/> means its condition is not yet met: the activity fails retryably with
/// <see cref="Delay"/> as the next retry's delay, so a wait holds no worker slot between reads (docs/04 section 8.2).
/// </summary>
public sealed record ActionResult(string Kind, StepReport? Report, string? Reason, TimeSpan Delay, IReadOnlyList<string> Events)
{
    public const string Wait = "wait";

    public static ActionResult Reported(StepReport report, IReadOnlyList<string> events) => new(StepOutcome.Reported, report, null, TimeSpan.Zero, events);

    public static ActionResult Failed(string reason, IReadOnlyList<string> events) => new(StepOutcome.WorkFailed, null, reason, TimeSpan.Zero, events);

    public static ActionResult CannotAnswer(string reason, IReadOnlyList<string> events) => new(StepOutcome.CannotAnswer, null, reason, TimeSpan.Zero, events);

    public static ActionResult Waiting(string reason, TimeSpan delay, IReadOnlyList<string> events) => new(Wait, null, reason, delay, events);

    /// <summary>The <c>findings</c> namespace after a findings action (<see cref="StepOutcome.Findings"/>).</summary>
    public IReadOnlyDictionary<string, long>? Findings { get; init; }

    /// <summary>The ledger version the counts were read at (<see cref="StepOutcome.FindingsVersion"/>).</summary>
    public long FindingsVersion { get; init; }

    public StepOutcome ToOutcome() => new(Kind, Report, Reason, Events) { Findings = Findings, FindingsVersion = FindingsVersion };
}

/// <summary>An infrastructure failure inside an action: the activity is retried and no attempt is used (docs/01 section 4).</summary>
public sealed class ActionInfrastructureException(string message) : Exception(message);
