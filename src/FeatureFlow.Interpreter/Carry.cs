using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>
/// A run's identity in a form that serialises: parameters are written as JSON text, which is YAML
/// (<see cref="YamlWriter"/>).
/// </summary>
public sealed record CarriedRunInfo(
    string Id,
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, string> Parameters,
    string? ParentId,
    string? TrackerIssue,
    string? ParentBranch,
    IReadOnlyDictionary<string, string>? DefaultBranches,
    IReadOnlyDictionary<string, string>? ParentArtifacts,
    IReadOnlyDictionary<string, string>? RootArtifacts,
    string? ParentContext = null)
{
    public static CarriedRunInfo From(RunInfo run) =>
        new(run.Id, run.Repositories.ToList(),
            new SortedDictionary<string, string>(run.Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Write(kv.Value)), StringComparer.Ordinal),
            run.ParentId, run.TrackerIssue, run.ParentBranch, Sorted(run.DefaultBranches), Sorted(run.ParentArtifacts), Sorted(run.RootArtifacts), run.ParentContext);

    public RunInfo ToRunInfo() =>
        new(Id, Repositories.ToList(),
            Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Read(kv.Value, $"params.{kv.Key}")),
            ParentId, TrackerIssue, ParentBranch, DefaultBranches, ParentArtifacts, RootArtifacts, ParentContext);

    static SortedDictionary<string, string>? Sorted(IReadOnlyDictionary<string, string>? map) =>
        map is null ? null : new SortedDictionary<string, string>(map.ToDictionary(), StringComparer.Ordinal);
}

public sealed record CarriedExecution(int Number, string? Outcome, IReadOnlyDictionary<string, string> Outputs, IReadOnlyDictionary<string, string> Artifacts)
{
    public bool Skipped { get; init; }

    public string? ActionAnswer { get; init; }
}

public sealed record CarriedStep(int Executions, int AttemptsUsed, int RacesUsed, long AttemptsGranted, CarriedExecution? Current, CarriedExecution? Previous)
{
    /// <summary>
    /// <see cref="StepRecord.EarlierArtifacts"/>; null when empty. State carried before it existed has none, and is read as the
    /// previous execution's artifacts, which is what <c>from: previous</c> read then.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EarlierArtifacts { get; init; }
}

public sealed record CarriedContainer(IReadOnlyList<int> BranchIndexes, IReadOnlyList<string> Branches, bool Active);

public sealed record CarriedWait(string Id, string Kind, string StepId, string? Outcome, string Reason, string? Patch, bool FromChild)
{
    public bool CleanUp { get; init; }
    public string? LibraryItem { get; init; }
    public string? LibraryContent { get; init; }
    public string? StepKey { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public bool Session { get; init; }
    public int Reminders { get; init; }
}

public sealed record CarriedEntry(string StepId, bool CleanUp, bool Resumes = false)
{
    public bool PastBreakpoint { get; init; }
    public string? From { get; init; }
}

public sealed record CarriedTrace(string Kind, string StepId, string? Detail);

/// <summary>
/// The core's state as plain serialisable data, carried across continue-as-new (docs/02 section 3; M2
/// follow-up 6). The plan is not carried: it is the template loaded again with the carried patches, as every
/// revision is. Output values and patches are written as JSON text.
/// </summary>
public sealed record CarriedCore(
    CarriedRunInfo Run,
    string Status,
    IReadOnlyList<string> Patches,
    IReadOnlyList<string> IdHistory,
    IReadOnlyDictionary<string, CarriedStep> Steps,
    IReadOnlyDictionary<string, CarriedContainer> Containers,
    IReadOnlyList<CarriedEntry> Pending,
    IReadOnlyList<string> InFlight,
    IReadOnlyList<CarriedWait> Waits,
    long RunAttempts,
    long RunCeilingGranted,
    IReadOnlyDictionary<string, long> Findings,
    IReadOnlyList<CarriedTrace> Trace,
    int NextWaitNumber)
{
    /// <summary>The ledger version the <see cref="Findings"/> counts were read at (review F3).</summary>
    public long FindingsVersion { get; init; }

    /// <summary>Steering (M7): breakpoints, skips not yet reached, overrides applied and the run's library overrides. Each null when empty.</summary>
    public IReadOnlyList<string>? Breakpoints { get; init; }

    public IReadOnlyDictionary<string, string>? Skips { get; init; }

    public IReadOnlyList<AppliedOverride>? Overrides { get; init; }

    public IReadOnlyList<LibraryOverrideEntry>? LibraryOverrides { get; init; }

    /// <summary>The waits that have closed, and how (<see cref="RunState.Closed"/>). Null when none has.</summary>
    public IReadOnlyList<ClosedWait>? Closed { get; init; }

    /// <summary>The child runs still running (<see cref="RunState.Children"/>). Null when there are none.</summary>
    public IReadOnlyList<ChildRun>? Children { get; init; }

    /// <summary>The run context's entries (<see cref="RunState.Context"/>). Null when there are none.</summary>
    public IReadOnlyList<ContextEntry>? Context { get; init; }

    /// <summary>How many plan revisions have been taken for the artifact index (<see cref="RunCore.TakeRevisions"/>).</summary>
    public int RevisionsTaken { get; init; }
}

public sealed partial class RunCore
{
    /// <summary>
    /// The state to carry. Commands not yet taken would be lost, so the shell takes them first
    /// (<see cref="Flush"/>); a snapshot with commands outstanding is refused.
    /// </summary>
    public CarriedCore Snapshot()
    {
        if (outbox.Count > 0)
            throw new InvalidOperationException("take the outstanding commands before taking a snapshot");
        static CarriedExecution? Exec(Execution? e) => e is null
            ? null
            : new CarriedExecution(e.Number, e.Outcome,
                new SortedDictionary<string, string>(e.Outputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)), StringComparer.Ordinal),
                new SortedDictionary<string, string>(e.Artifacts, StringComparer.Ordinal)) { Skipped = e.Skipped, ActionAnswer = e.ActionAnswer };
        var s = State;
        return new CarriedCore(
            CarriedRunInfo.From(Run),
            s.Status.Name(),
            s.Patches.Select(PatchReader.Write).ToList(),
            s.IdHistory.ToList(),
            new SortedDictionary<string, CarriedStep>(s.Steps.ToDictionary(kv => kv.Key,
                kv => new CarriedStep(kv.Value.Executions, kv.Value.AttemptsUsed, kv.Value.RacesUsed, kv.Value.AttemptsGranted, Exec(kv.Value.Current), Exec(kv.Value.Previous))
                {
                    EarlierArtifacts = kv.Value.EarlierArtifacts.Count == 0 ? null : new SortedDictionary<string, string>(kv.Value.EarlierArtifacts, StringComparer.Ordinal),
                }), StringComparer.Ordinal),
            new SortedDictionary<string, CarriedContainer>(s.Containers.ToDictionary(kv => kv.Key,
                kv => new CarriedContainer(kv.Value.BranchIndexes.ToList(), kv.Value.Branches.Select(b => b.ToString()).ToList(), kv.Value.Active)), StringComparer.Ordinal),
            s.Pending.Select(e => new CarriedEntry(e.StepId, e.CleanUp, e.Resumes) { PastBreakpoint = e.PastBreakpoint, From = e.From }).ToList(),
            s.InFlight.ToList(),
            s.Waits.Select(w => new CarriedWait(w.Id, w.Kind.ToString(), w.StepId, w.Outcome, w.Reason, w.Patch is null ? null : PatchReader.Write(w.Patch), w.FromChild)
            {
                CleanUp = w.CleanUp,
                LibraryItem = w.LibraryItem,
                LibraryContent = w.LibraryContent,
                StepKey = w.StepKey,
                Options = w.Options?.ToList(),
                Deadline = w.Deadline,
                Session = w.Session,
                Reminders = w.Reminders,
            }).ToList(),
            s.RunAttempts,
            s.RunCeilingGranted,
            new SortedDictionary<string, long>(s.Findings, StringComparer.Ordinal),
            s.Trace.Select(t => new CarriedTrace(t.Kind, t.StepId, t.Detail)).ToList(),
            s.NextWaitNumber)
        {
            FindingsVersion = s.FindingsVersion,
            Breakpoints = s.Breakpoints.Count == 0 ? null : s.Breakpoints.ToList(),
            Skips = s.Skips.Count == 0 ? null : new SortedDictionary<string, string>(s.Skips, StringComparer.Ordinal),
            Overrides = s.Overrides.Count == 0 ? null : s.Overrides.ToList(),
            LibraryOverrides = s.LibraryOverrides.Count == 0 ? null : s.LibraryOverrides.ToList(),
            Closed = s.Closed.Count == 0 ? null : s.Closed.ToList(),
            Children = s.Children.Count == 0 ? null : s.Children.ToList(),
            Context = s.Context.Count == 0 ? null : s.Context.ToList(),
            RevisionsTaken = revisionsTaken,
        };
    }

    /// <summary>
    /// Rebuilds a core from carried state: the template is loaded with the carried patches, and the state is
    /// restored as it was. Nothing is entered; the shell calls <see cref="Advance"/>.
    /// </summary>
    public static RunCore Resume(LibrarySet libraries, LibraryReference template, CarriedCore carried, RunOptions? options = null, IFindingsSource? findings = null)
    {
        var run = carried.Run.ToRunInfo();
        var patches = carried.Patches.Select(PatchReader.ReadWritten).ToList();
        var diagnostics = new DiagnosticBag();
        var plan = Load(libraries, template, run, patches, diagnostics);
        if (plan is null || diagnostics.HasErrors)
            throw new InvalidOperationException("the carried plan does not load: " + string.Join("; ", diagnostics.Where(d => d.Blocks).Take(5)));

        var core = new RunCore(plan, run, options ?? new RunOptions(), libraries, template, findings);
        var s = core.State;
        s.Status = RunStatuses.Parse(carried.Status) ?? throw new InvalidOperationException($"unknown carried status {carried.Status}");
        s.Patches.AddRange(patches);
        s.IdHistory.Clear();
        foreach (var id in carried.IdHistory)
            s.IdHistory.Add(id);
        foreach (var (id, step) in carried.Steps)
        {
            var record = new StepRecord
            {
                Executions = step.Executions,
                AttemptsUsed = step.AttemptsUsed,
                RacesUsed = step.RacesUsed,
                AttemptsGranted = step.AttemptsGranted,
                Current = Exec(step.Current),
                Previous = Exec(step.Previous),
            };
            foreach (var (name, reference) in step.EarlierArtifacts ?? step.Previous?.Artifacts ?? new Dictionary<string, string>())
                record.EarlierArtifacts[name] = reference;
            s.Steps[id] = record;
        }
        foreach (var (id, container) in carried.Containers)
            s.Containers[id] = new ContainerRecord
            {
                BranchIndexes = container.BranchIndexes.ToList(),
                Branches = container.Branches.Select(Enum.Parse<BranchStatus>).ToList(),
                Active = container.Active,
            };
        s.Pending.AddRange(carried.Pending.Select(e => new Entry(e.StepId, e.CleanUp, e.Resumes, e.PastBreakpoint, e.From)));
        s.InFlight.AddRange(carried.InFlight);
        s.Waits.AddRange(carried.Waits.Select(w => new OpenWait(w.Id, Enum.Parse<WaitKind>(w.Kind), w.StepId, w.Outcome, w.Reason,
            w.Patch is null ? null : PatchReader.ReadWritten(w.Patch), w.FromChild)
        {
            CleanUp = w.CleanUp,
            LibraryItem = w.LibraryItem,
            LibraryContent = w.LibraryContent,
            StepKey = w.StepKey,
            Options = w.Options,
            Deadline = w.Deadline,
            Session = w.Session,
            Reminders = w.Reminders,
        }));
        s.RunAttempts = carried.RunAttempts;
        s.RunCeilingGranted = carried.RunCeilingGranted;
        foreach (var (name, count) in carried.Findings)
            s.Findings[name] = count;
        s.FindingsVersion = carried.FindingsVersion;
        s.Trace.AddRange(carried.Trace.Select(t => new TraceEntry(t.Kind, t.StepId, t.Detail)));
        s.NextWaitNumber = carried.NextWaitNumber;
        foreach (var id in carried.Breakpoints ?? [])
            s.Breakpoints.Add(id);
        foreach (var (id, outcome) in carried.Skips ?? new Dictionary<string, string>())
            s.Skips[id] = outcome;
        s.Overrides.AddRange(carried.Overrides ?? []);
        s.LibraryOverrides.AddRange(carried.LibraryOverrides ?? []);
        s.Closed.AddRange(carried.Closed ?? []);
        s.Children.AddRange(carried.Children ?? []);
        s.Context.AddRange(carried.Context ?? []);
        core.revisionsTaken = carried.RevisionsTaken;
        return core;

        static Execution? Exec(CarriedExecution? e)
        {
            if (e is null)
                return null;
            var execution = new Execution { Number = e.Number, Outcome = e.Outcome, Skipped = e.Skipped, ActionAnswer = e.ActionAnswer };
            foreach (var (name, value) in e.Outputs)
                execution.Outputs[name] = YamlWriter.ReadPlain(value);
            foreach (var (name, reference) in e.Artifacts)
                execution.Artifacts[name] = reference;
            return execution;
        }
    }
}
