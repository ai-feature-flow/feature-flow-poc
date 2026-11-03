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
    string? ParentBranch = null,
    IReadOnlyDictionary<string, string>? DefaultBranches = null)
{
    public static CarriedRunInfo From(RunInfo run) =>
        new(run.Id, run.Repositories.ToList(),
            new SortedDictionary<string, string>(run.Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Write(kv.Value)), StringComparer.Ordinal),
            run.ParentId, run.TrackerIssue, run.ParentBranch,
            run.DefaultBranches is null ? null : new SortedDictionary<string, string>(run.DefaultBranches.ToDictionary(), StringComparer.Ordinal));

    public RunInfo ToRunInfo() =>
        new(Id, Repositories.ToList(),
            Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Read(kv.Value, $"params.{kv.Key}")),
            ParentId, TrackerIssue, ParentBranch, DefaultBranches);
}

public sealed record CarriedExecution(int Number, string? Outcome, IReadOnlyDictionary<string, string> Outputs, IReadOnlyDictionary<string, string> Artifacts);

public sealed record CarriedStep(int Executions, int AttemptsUsed, int RacesUsed, long AttemptsGranted, CarriedExecution? Current, CarriedExecution? Previous);

public sealed record CarriedContainer(IReadOnlyList<int> BranchIndexes, IReadOnlyList<string> Branches, bool Active);

public sealed record CarriedWait(string Id, string Kind, string StepId, string? Outcome, string Reason, string? Patch, bool FromChild);

public sealed record CarriedEntry(string StepId, bool CleanUp, bool Resumes = false);

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
    int NextWaitNumber);

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
                new SortedDictionary<string, string>(e.Artifacts, StringComparer.Ordinal));
        var s = State;
        return new CarriedCore(
            CarriedRunInfo.From(Run),
            s.Status.Name(),
            s.Patches.Select(PatchReader.Write).ToList(),
            s.IdHistory.ToList(),
            new SortedDictionary<string, CarriedStep>(s.Steps.ToDictionary(kv => kv.Key,
                kv => new CarriedStep(kv.Value.Executions, kv.Value.AttemptsUsed, kv.Value.RacesUsed, kv.Value.AttemptsGranted, Exec(kv.Value.Current), Exec(kv.Value.Previous))), StringComparer.Ordinal),
            new SortedDictionary<string, CarriedContainer>(s.Containers.ToDictionary(kv => kv.Key,
                kv => new CarriedContainer(kv.Value.BranchIndexes.ToList(), kv.Value.Branches.Select(b => b.ToString()).ToList(), kv.Value.Active)), StringComparer.Ordinal),
            s.Pending.Select(e => new CarriedEntry(e.StepId, e.CleanUp, e.Resumes)).ToList(),
            s.InFlight.ToList(),
            s.Waits.Select(w => new CarriedWait(w.Id, w.Kind.ToString(), w.StepId, w.Outcome, w.Reason, w.Patch is null ? null : PatchReader.Write(w.Patch), w.FromChild)).ToList(),
            s.RunAttempts,
            s.RunCeilingGranted,
            new SortedDictionary<string, long>(s.Findings, StringComparer.Ordinal),
            s.Trace.Select(t => new CarriedTrace(t.Kind, t.StepId, t.Detail)).ToList(),
            s.NextWaitNumber);
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
            s.Steps[id] = new StepRecord
            {
                Executions = step.Executions,
                AttemptsUsed = step.AttemptsUsed,
                RacesUsed = step.RacesUsed,
                AttemptsGranted = step.AttemptsGranted,
                Current = Exec(step.Current),
                Previous = Exec(step.Previous),
            };
        foreach (var (id, container) in carried.Containers)
            s.Containers[id] = new ContainerRecord
            {
                BranchIndexes = container.BranchIndexes.ToList(),
                Branches = container.Branches.Select(Enum.Parse<BranchStatus>).ToList(),
                Active = container.Active,
            };
        s.Pending.AddRange(carried.Pending.Select(e => new Entry(e.StepId, e.CleanUp, e.Resumes)));
        s.InFlight.AddRange(carried.InFlight);
        s.Waits.AddRange(carried.Waits.Select(w => new OpenWait(w.Id, Enum.Parse<WaitKind>(w.Kind), w.StepId, w.Outcome, w.Reason,
            w.Patch is null ? null : PatchReader.ReadWritten(w.Patch), w.FromChild)));
        s.RunAttempts = carried.RunAttempts;
        s.RunCeilingGranted = carried.RunCeilingGranted;
        foreach (var (name, count) in carried.Findings)
            s.Findings[name] = count;
        s.Trace.AddRange(carried.Trace.Select(t => new TraceEntry(t.Kind, t.StepId, t.Detail)));
        s.NextWaitNumber = carried.NextWaitNumber;
        return core;

        static Execution? Exec(CarriedExecution? e)
        {
            if (e is null)
                return null;
            var execution = new Execution { Number = e.Number, Outcome = e.Outcome };
            foreach (var (name, value) in e.Outputs)
                execution.Outputs[name] = YamlWriter.ReadPlain(value);
            foreach (var (name, reference) in e.Artifacts)
                execution.Artifacts[name] = reference;
            return execution;
        }
    }
}
