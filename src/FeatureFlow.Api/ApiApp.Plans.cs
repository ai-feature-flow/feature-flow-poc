using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
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
        /// The plans most recently loaded, by lock and revision artifact: both are content-addressed, so the plan they load never
        /// changes. A tree re-read on every change of any of its runs loads each plan once.
        /// </summary>
        readonly Dictionary<string, LoadedPlan> plansByRevision = new(StringComparer.Ordinal);
        readonly Queue<string> planOrder = new();
        const int LoadedPlansKept = 128;

        /// <summary>
        /// A run's plan (G11; LOG 404): revision <paramref name="revision"/> it indexed, else the latest, rebuilt with the
        /// libraries locked when its root run started, and with <paramref name="extraPatch"/> (as <c>PatchReader.Write</c>
        /// writes it) applied after its own patches where given; or why not. <paramref name="artifacts"/> are the artifacts already read
        /// for the run (a tree reads its root's, which hold every descendant's, once), else they are read now.
        /// </summary>
        async Task<(LoadedPlan? Plan, PlanRefusal? Refusal)> LoadPlanAsync(RunRecord run, int? revision, string? extraPatch, CancellationToken ct,
            IReadOnlyList<ArtifactRecord>? artifacts = null)
        {
            var revisions = (artifacts ?? await PlanRevisionsAsync(run.RunId, ct)).Where(a => a.RunId == run.RunId && a is { StepId: "", Name: ArtifactNames.PlanRevision });
            var chosen = revision is { } r ? revisions.LastOrDefault(a => a.Execution == r) : revisions.MaxBy(a => a.Execution);
            if (chosen is null)
                return (null, new PlanRefusal(204, revision is null ? $"run {run.RunId} has no plan yet" : $"run {run.RunId} has no revision {revision}"));
            if (await LockOfAsync(run, ct) is not { } lockRef)
                return (null, new PlanRefusal(404, $"run {run.RunId} has no library lock to show its plan from"));
            var key = $"{lockRef}|{chosen.Ref}";
            if (extraPatch is null)
                lock (librariesGate)
                    if (plansByRevision.TryGetValue(key, out var cached))
                        return (cached, null);
            var d = new DiagnosticBag();
            var libraries = await LockedLibrariesAsync(lockRef, d, ct);
            var source = PlanSource.Read(await ReadTextAsync(chosen.Ref, ct));
            if (extraPatch is not null)
                source = source with { Patches = [.. source.Patches, extraPatch] };
            Plan? loaded;
            try
            {
                loaded = source.Load(libraries, d);
            }
            catch (FormatException e)
            {
                return (null, new PlanRefusal(409, $"revision {source.Revision} of run {run.RunId} does not load: {e.Message}"));
            }

            // A proposed patch that does not apply leaves the plan as it was, with the reason: it is refused, not shown unchanged.
            if (loaded is { } plan && (extraPatch is null || !d.Any(x => x.Blocks)))
            {
                var result = new LoadedPlan(plan, source, libraries);
                if (extraPatch is null)
                    lock (librariesGate)
                        if (plansByRevision.TryAdd(key, result))
                        {
                            planOrder.Enqueue(key);
                            while (planOrder.Count > LoadedPlansKept)
                                plansByRevision.Remove(planOrder.Dequeue());
                        }

                return (result, null);
            }

            return (null, new PlanRefusal(409, $"revision {source.Revision} of run {run.RunId} does not load: {string.Join("; ", d.Where(x => x.Blocks).Take(3).Select(x => x.Message))}"));
        }

        /// <summary>The artifacts of the run and every descendant run, of which a plan's revisions are those under no step.</summary>
        async Task<IReadOnlyList<ArtifactRecord>> PlanRevisionsAsync(string runId, CancellationToken ct) =>
            (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = runId }, ct)).Unwrap()
            .Where(a => a is { StepId: "", Name: ArtifactNames.PlanRevision }).ToList();

        /// <summary>What a run's plan view is read with: its timeline, open waits, step counters and child runs.</summary>
        sealed record RunState(IReadOnlyList<TimelineRecord> Timeline, IReadOnlyList<WaitRecord> OpenWaits, IReadOnlyList<StepStateRecord> StepStates,
            IReadOnlyList<RunRecord> Children);

        async Task<RunState> RunStateAsync(string runId, CancellationToken ct, IReadOnlyList<RunRecord>? children = null)
        {
            var p = new Dictionary<string, string> { ["run_id"] = runId };
            return new RunState(
                (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap(),
                (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, ct)).Unwrap(),
                (await Records.QueryAsync<StepStateRecord>(Queries.StepStates, p, ct)).Unwrap(),
                children ?? (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = runId }, ct)).Unwrap());
        }

        /// <summary>
        /// The plan as the API shows it (A2): its readable form, its groups, and each step with its status, steering, the group or
        /// step it sits in, its counters, the child run it started and when its status last changed. Given the <paramref name="current"/>
        /// plan, <paramref name="loaded"/> is a proposed one (A8): each step that differs is marked, and a step it removes is kept in
        /// place, marked removed, after the step it followed.
        /// </summary>
        static PlanView PlanViewOf(string runId, LoadedPlan loaded, RunState state, LoadedPlan? current = null, int? revision = null)
        {
            var statuses = Statuses(state.Timeline, state.OpenWaits);
            var (breakpoints, skips) = Steering(state.Timeline);
            var counters = state.StepStates.GroupBy(s => s.StepId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var childRuns = state.Children.Where(c => c.ParentStepId is not null).OrderBy(c => c.StartedAt ?? c.UpdatedAt)
                .GroupBy(c => c.ParentStepId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().RunId, StringComparer.Ordinal);
            var changedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var t in state.Timeline.Where(t => t.StepId.Length > 0))
                changedAt[t.StepId] = t.At;

            PlanStepView ViewOf(Step s, PlanChange? change)
            {
                var c = counters.GetValueOrDefault(s.Id);
                return new PlanStepView(s.Id, s.Scope.ContainerId, FormOf(s), ReadableForm.RoleOrAction(s),
                    s.Outcomes().Select(o => new PlanOutcomeView(o.Name, o.Class.Name(),
                        s.ResolvedRoutes.FirstOrDefault(r => r.Outcome == o.Name) is { } route ? ReadableForm.Show(route.Target) : null,
                        s.Produces.Where(p => p.Outcome is null || p.Outcome == o.Name).Select(p => p.Name).ToList())).ToList(),
                    s.Protected, statuses.GetValueOrDefault(s.Id) ?? "pending", breakpoints.Contains(s.Id), skips.GetValueOrDefault(s.Id),
                    GroupIdOf(s.Scope), c?.Executions ?? 0, c?.AttemptsUsed, c?.AttemptsAllowed, c?.AttemptsGranted ?? 0, childRuns.GetValueOrDefault(s.Id),
                    changedAt.TryGetValue(s.Id, out var at) ? at : null, change,
                    s.Source?["with"] is { } with ? RunCore.BlockYaml(with) : null, s.Source?["limits"] is { } limits ? RunCore.BlockYaml(limits) : null);
            }

            var plan = loaded.Plan;
            var groups = GroupsOf(plan);
            List<PlanStepView> steps;
            if (current is null)
                steps = plan.AllSteps.Select(s => ViewOf(s, null)).ToList();
            else
            {
                var was = current.Plan.AllSteps.ToDictionary(s => s.Id, StringComparer.Ordinal);
                var before = was.Values.ToDictionary(s => s.Id, s => ViewOf(s, null), StringComparer.Ordinal);
                steps = plan.AllSteps.Select(s => ViewOf(s,
                        !before.TryGetValue(s.Id, out var old) ? PlanChange.Added : SameStep(old, ViewOf(s, null), was[s.Id], s) ? null : PlanChange.Changed))
                    .ToList();
                // A removed step is kept where it was: after the nearest step before it that the proposed plan keeps (or first).
                string? anchor = null;
                var inserted = 0;
                foreach (var s in current.Plan.AllSteps)
                {
                    if (plan.StepById(s.Id) is not null)
                    {
                        (anchor, inserted) = (s.Id, 0);
                        continue;
                    }

                    var at = anchor is null ? 0 : steps.FindIndex(v => v.Id == anchor) + 1;
                    steps.Insert(at + inserted++, before[s.Id] with { Change = PlanChange.Removed });
                }

                foreach (var g in GroupsOf(current.Plan).Where(g => groups.All(h => h.Id != g.Id)))
                    groups.Add(g);
            }

            return new PlanView(runId, revision ?? loaded.Source.Revision, loaded.Source.Template, ReadableForm.Render(plan, statuses), steps, groups);
        }

        /// <summary>Whether a step is the same in two plans: its form, what it does, its outcomes and their routes, and the step as written.</summary>
        static bool SameStep(PlanStepView a, PlanStepView b, Step x, Step y) =>
            a.Form == b.Form && a.What == b.What && a.Outcomes.Select(o => (o.Name, o.Class, o.Route)).SequenceEqual(b.Outcomes.Select(o => (o.Name, o.Class, o.Route)))
            && (x.Source is null ? "" : YamlWriter.Write(x.Source)) == (y.Source is null ? "" : YamlWriter.Write(y.Source));

        /// <summary>The group a scope's steps sit in: a fragment's use, or a branch (<c>&lt;container&gt;#&lt;index&gt;</c>); null at the template's top level.</summary>
        static string? GroupIdOf(Scope scope) => scope.Kind switch
        {
            ScopeKind.Fragment => scope.UseId ?? scope.Id,
            ScopeKind.Branch => $"{scope.ContainerId}#{scope.BranchIndex}",
            _ => null,
        };

        /// <summary>
        /// The plan's groups, each after the group it sits in: one per fragment use (labelled with its id as used), and one per
        /// branch of a <c>for_each</c> or <c>parallel</c> (labelled with its item, else its only step, else its number), whose
        /// parent is its container step.
        /// </summary>
        static List<PlanGroupView> GroupsOf(Plan plan)
        {
            var branches = new Dictionary<string, Branch>(StringComparer.Ordinal);
            foreach (var step in plan.AllSteps)
                foreach (var branch in step.Container?.Branches ?? [])
                    branches[$"{step.Id}#{branch.Index}"] = branch;
            var groups = new List<PlanGroupView>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string? Add(Scope scope)
            {
                if (GroupIdOf(scope) is not { } id)
                    return null;
                if (seen.Contains(id))
                    return id;
                if (scope.Kind == ScopeKind.Fragment)
                {
                    var parent = Add(scope.Parent!);
                    var prefix = scope.Parent!.Prefix;
                    groups.Add(new PlanGroupView(id, parent, GroupKind.Fragment, id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id, null));
                }
                else
                {
                    var branch = branches.GetValueOrDefault(id);
                    var item = (branch?.Scope.Item ?? scope.Item) is { } node ? node is YScalar s ? s.Text : YamlWriter.Write(node) : null;
                    var label = (branch?.Scope.Item ?? scope.Item) is { } i ? ItemLabel(i)
                        : branch?.Steps is [var only] ? only.LocalId
                        : $"branch {scope.BranchIndex + 1}";
                    groups.Add(new PlanGroupView(id, scope.ContainerId, GroupKind.Branch, label, item));
                }

                seen.Add(id);
                return id;
            }

            foreach (var step in plan.AllSteps)
                Add(step.Scope);
            return groups;
        }

        /// <summary>A <c>for_each</c> item as a label: a scalar's text, else a map's <c>name</c>, <c>id</c>, <c>key</c> or <c>title</c>, else the item as YAML.</summary>
        static string ItemLabel(YNode item) => item switch
        {
            YScalar s => s.Text,
            YMap m when new[] { "name", "id", "key", "title" }.Select(k => m[k]).OfType<YScalar>().FirstOrDefault() is { } named => named.Text,
            _ => YamlWriter.Write(item),
        };

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
