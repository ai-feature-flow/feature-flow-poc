using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;
using Step = FeatureFlow.Model.Plans.Step;
using StepForm = FeatureFlow.Api.Contracts.StepForm;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's plan revision as loaded: the plan, the revision it was built from, and the libraries it was built with.</summary>
    sealed record LoadedPlan(Plan Plan, PlanSource Source, LibrarySet Libraries);

    /// <summary>
    /// Why a run's plan was not loaded: 204 (the run has indexed no revision yet, which is not a refusal, LOG 493), 404 (no
    /// library lock to load it with) or 409 (the revision does not load), with a message.
    /// </summary>
    sealed record PlanRefusal(int Status, string Message);

    sealed partial class Handlers
    {
        /// <summary>The library sets most recently loaded, by lock reference: a lock is content-addressed, so its libraries never change.</summary>
        readonly Dictionary<string, (LibrarySet Libraries, IReadOnlyList<Diagnostic> Diagnostics)> librariesByLock = new(StringComparer.Ordinal);
        readonly Queue<string> lockOrder = new();
        readonly Lock librariesGate = new();
        const int LockedLibrariesKept = 32;

        /// <summary>
        /// A run's plan (G11; LOG 404): revision <paramref name="revision"/> it indexed, else the latest, rebuilt with the
        /// libraries locked when its root run started, and with <paramref name="extraPatch"/> (as <c>PatchReader.Write</c>
        /// writes it) applied after its own patches where given; or why not.
        /// </summary>
        async Task<(LoadedPlan? Plan, PlanRefusal? Refusal)> LoadPlanAsync(RunRecord run, int? revision, string? extraPatch, CancellationToken ct)
        {
            var revisions = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap()
                .Where(a => a.RunId == run.RunId && a is { StepId: "", Name: ArtifactNames.PlanRevision });
            var chosen = revision is { } r ? revisions.LastOrDefault(a => a.Execution == r) : revisions.MaxBy(a => a.Execution);
            if (chosen is null)
                return (null, new PlanRefusal(204, revision is null ? $"run {run.RunId} has no plan yet" : $"run {run.RunId} has no revision {revision}"));
            if (await LockOfAsync(run, ct) is not { } lockRef)
                return (null, new PlanRefusal(404, $"run {run.RunId} has no library lock to show its plan from"));
            var d = new DiagnosticBag();
            var libraries = await LockedLibrariesAsync(lockRef, d, ct);
            var source = PlanSource.Read(await ReadTextAsync(chosen.Ref, ct));
            if (extraPatch is not null)
                source = source with { Patches = [.. source.Patches, extraPatch] };
            return source.Load(libraries, d) is { } plan
                ? (new LoadedPlan(plan, source, libraries), null)
                : (null, new PlanRefusal(409, $"revision {source.Revision} of run {run.RunId} does not load: {string.Join("; ", d.Where(x => x.Blocks).Take(3).Select(x => x.Message))}"));
        }

        /// <summary>The libraries of a lock, from the cache or read now; what reading them reported is added to <paramref name="diagnostics"/> either way.</summary>
        async Task<LibrarySet> LockedLibrariesAsync(string lockRef, DiagnosticBag diagnostics, CancellationToken ct)
        {
            lock (librariesGate)
                if (librariesByLock.TryGetValue(lockRef, out var cached))
                {
                    diagnostics.AddRange(cached.Diagnostics);
                    return cached.Libraries;
                }

            var d = new DiagnosticBag();
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await ReadTextAsync(lockRef, ct), Stored)!;
            var libraries = new List<Library.Library>();
            foreach (var locked in lockDocument.Libraries)
            {
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (path, hash) in locked.Files)
                    files[path] = await ReadTextAsync(hash, ct);
                if (Library.Library.FromFiles(new LibraryFiles(files), d) is { } library)
                    libraries.Add(library);
            }

            var set = new LibrarySet(libraries);
            lock (librariesGate)
            {
                if (librariesByLock.TryAdd(lockRef, (set, d.ToList())))
                {
                    lockOrder.Enqueue(lockRef);
                    while (lockOrder.Count > LockedLibrariesKept)
                        librariesByLock.Remove(lockOrder.Dequeue());
                }
            }

            diagnostics.AddRange(d);
            return set;
        }

        static StepForm FormOf(Step step) => ReadableForm.Form(step) switch
        {
            "agent" => StepForm.Agent,
            "decision" => StepForm.Decision,
            "action" => StepForm.Action,
            "chain" => StepForm.Chain,
            "for_each" => StepForm.ForEach,
            _ => StepForm.Parallel,
        };

        /// <summary>
        /// The breakpoints set on steps, and the outcome each step is skipped with, from the run's timeline: what the core
        /// accepted, not what was asked (a refused override changes nothing).
        /// </summary>
        static (HashSet<string> Breakpoints, Dictionary<string, string> Skips) Steering(IEnumerable<TimelineRecord> timeline)
        {
            var breakpoints = new HashSet<string>(StringComparer.Ordinal);
            var skips = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in timeline.Where(t => t.StepId.Length > 0))
                switch (t.Kind)
                {
                    case "breakpoint_set":
                        breakpoints.Add(t.StepId);
                        break;
                    case "breakpoint_cleared":
                        breakpoints.Remove(t.StepId);
                        break;
                    case "skip_set" or "skipped" when t.Detail is { Length: > 0 } detail:
                        skips[t.StepId] = detail.Split(' ')[0];
                        break;
                }

            return (breakpoints, skips);
        }

        /// <summary>The lock of the run's tree: children run on their root's lock.</summary>
        async Task<string?> LockOfAsync(RunRecord run, CancellationToken ct)
        {
            for (var (current, depth) = (run, 0); current is not null && depth < 32; depth++)
            {
                if ((await Records.QueryAsync<LockRecord>(Queries.Lock, new Dictionary<string, string> { ["run_id"] = current.RunId }, ct)).Unwrap().FirstOrDefault() is { } found)
                    return found.LockRef;
                current = current.ParentRunId is { } parent
                    ? (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = parent }, ct)).Unwrap().FirstOrDefault()
                    : null;
            }

            return null;
        }

        async Task<string> ReadTextAsync(string reference, CancellationToken ct)
        {
            await using var stream = (await Artifacts.GetAsync(new ArtifactRef(reference), ct)).Unwrap();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>Each step's latest status from the timeline, an open wait overriding it.</summary>
        static Dictionary<string, string> Statuses(IEnumerable<TimelineRecord> timeline, IEnumerable<WaitRecord> waits)
        {
            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in timeline.Where(t => t.StepId.Length > 0))
                if (t.Kind switch
                    {
                        "started" or "resumed" => "running",
                        "completed" => $"done({t.Detail})",
                        "auto_resolved" => "done(auto)",
                        "retried" or "attempt_failed" => "retrying",
                        "interrupted" or "escalated" or "cancelled" or "exhausted" => t.Kind,
                        _ => null,
                    } is { } status)
                    statuses[t.StepId] = status;
            foreach (var w in waits.Where(w => w.StepId.Length > 0))
                statuses[w.StepId] = $"waiting({w.Kind})";
            return statuses;
        }
    }
}
