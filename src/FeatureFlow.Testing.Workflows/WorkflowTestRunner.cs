using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Testing.Workflows;

/// <summary>The result of one workflow test: whether it passed, why not, and what the run did.</summary>
public sealed record WorkflowTestResult(
    string Name,
    string File,
    IReadOnlyList<string> Failures,
    string Status,
    IReadOnlyList<string> Path,
    IReadOnlyList<TraceEntry> Trace)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Re-drives a template from a workflow test file (docs/09 section 4.3): the interpreter's core runs the plan
/// with no Temporal and no providers, and each step's outcome comes from the script. Steps finish one at a time,
/// in the order they started, so a run is deterministic. A step the script does not list takes its first
/// success outcome; an output it does not list takes its type's zero value (LOG 75).
/// </summary>
public static class WorkflowTestRunner
{
    /// <summary>Inputs given before the runner gives up on a run that does not settle, such as an unbounded loop.</summary>
    public const int MaxInputs = 5000;

    /// <summary>Runs every <c>*.yaml</c> test in a folder, or one file, against the library holding it and any others.</summary>
    public static IReadOnlyList<WorkflowTestResult> RunPath(string path, IEnumerable<string>? otherLibraries = null)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.yaml").Order(StringComparer.Ordinal).ToList()
            : [path];
        var results = new List<WorkflowTestResult>();
        foreach (var file in files)
        {
            var diagnostics = new DiagnosticBag();
            var libraries = OpenLibraries(file, otherLibraries ?? [], diagnostics);
            var test = WorkflowTestReader.Read(file, diagnostics);
            if (test is null || libraries is null || diagnostics.HasErrors)
            {
                results.Add(new WorkflowTestResult(test?.Name ?? System.IO.Path.GetFileNameWithoutExtension(file), file,
                    diagnostics.Where(x => x.Blocks).Select(x => x.ToString()).DefaultIfEmpty("the test file could not be read").ToList(), "", [], []));
                continue;
            }

            results.Add(Run(test, libraries));
        }

        return results;
    }

    /// <summary>The library a test file belongs to (the folder above <c>tests/</c>), and any others named.</summary>
    static LibrarySet? OpenLibraries(string file, IEnumerable<string> others, DiagnosticBag d)
    {
        var list = new List<Library.Library>();
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        var owner = System.IO.Path.GetDirectoryName(folder);
        if (owner is not null && System.IO.File.Exists(System.IO.Path.Combine(owner, "library.yaml")) && Library.Library.Open(owner, d) is { } own)
            list.Add(own);
        foreach (var root in others)
            if (Library.Library.Open(root, d) is { } other)
                list.Add(other);
        return list.Count == 0 ? null : new LibrarySet(list);
    }

    public static WorkflowTestResult Run(WorkflowTest test, LibrarySet libraries, RunOptions? options = null)
    {
        var failures = new List<string>();
        var observedChecks = new SortedSet<int>();
        var findings = new ScriptedFindings(test.Findings);
        var run = new RunInfo($"test-{test.Name}", test.Repositories, test.Parameters);
        var (core, diagnostics) = RunCore.Start(libraries, test.Template, run, options, findings);
        foreach (var d in diagnostics.Where(d => d.Blocks))
            observedChecks.Add(d.Check);

        if (core is null)
        {
            CompareChecks(test, observedChecks, failures, diagnostics);
            if (test.ExpectValidationErrors.Count == 0)
                failures.Add("the template did not load: " + string.Join("; ", diagnostics.Where(d => d.Blocks).Take(5)));
            return new WorkflowTestResult(test.Name, test.File, failures, "", [], []);
        }

        var patchDiagnostics = new List<Diagnostic>();
        var patchPending = test.Patch is not null;
        if (test.Patch is { ProposedBy.StepId: null })
        {
            // The test file stands in for the API, which names the authenticated proposer (F8).
            Record(core.Apply(new PatchProposed(test.Patch, test.Patch.ProposedBy)), observedChecks, patchDiagnostics, failures);
            patchPending = false;
        }

        var queue = new List<(string StepId, int Execution, bool Decision)>();
        var cleanUps = new List<string>();
        var tries = new Dictionary<(string, int), int>();
        var inputs = 0;
        while (true)
        {
            foreach (var command in core.Advance())
            {
                switch (command)
                {
                    case StartStep s:
                        queue.Add((s.StepId, s.Execution, false));
                        break;
                    case OpenDecision o:
                        queue.Add((o.StepId, o.Execution, true));
                        break;
                    case CancelStep c:
                        queue.RemoveAll(q => q.StepId == c.StepId);
                        break;
                    case CleanUp c:
                        cleanUps.Add(c.StepId);
                        break;
                }
            }

            if (queue.Count == 0 || core.State.Status != RunStatus.Running)
                break;
            if (++inputs > MaxInputs)
            {
                failures.Add($"the run did not settle within {MaxInputs} step results: an unbounded loop?");
                break;
            }

            var (stepId, execution, decision) = queue[0];
            queue.RemoveAt(0);
            var step = core.StepById(stepId)!;
            var visits = test.Script.GetValueOrDefault(stepId);
            var visit = visits is null ? new Visit(null) : visits[Math.Min(execution - 1, visits.Count - 1)];
            var outcome = visit.Outcome ?? FirstSuccess(step);
            if (outcome is null)
            {
                failures.Add($"{stepId} has no success outcome to take by default; script it");
                break;
            }

            RunInput input;
            if (decision)
                input = new DecisionAnswered(stepId, outcome);
            else if (visit.Error == "infrastructure" && tries.GetValueOrDefault((stepId, execution)) < visit.Times)
            {
                tries[(stepId, execution)] = tries.GetValueOrDefault((stepId, execution)) + 1;
                input = new StepFailed(stepId, ErrorClass.Infrastructure, "scripted infrastructure failure");
            }
            else if (visit.Error == "work")
                input = new StepFailed(stepId, ErrorClass.Work, "scripted work failure");
            else
            {
                PatchDocument? patch = null;
                if (patchPending && test.Patch!.ProposedBy.StepId == stepId)
                {
                    patch = test.Patch;
                    patchPending = false;
                }
                else if (step.Action == "plan.expand")
                {
                    var (built, error) = PlanExpansion.Build(core, stepId, test.Expand.GetValueOrDefault(stepId) ?? []);
                    if (error is not null)
                    {
                        failures.Add(error);
                        break;
                    }

                    patch = built;
                }

                var outputs = Outputs(step, test.Outputs.GetValueOrDefault(stepId), execution, failures);
                var artifacts = step.Produces
                    .Where(p => p.Outcome == outcome || (p.Outcome is null && step.Outcomes().Any(o => o.Name == outcome && o.Class == OutcomeClass.Success)))
                    .Select(p => p.Name).Distinct()
                    .ToDictionary(n => n, n => $"artifact:{stepId}/{n}/{execution}");
                input = new StepCompleted(stepId, outcome, outputs, artifacts, patch);
            }

            var result = core.Apply(input);
            if (!result.Accepted && result.Patch is { Status: PatchStatus.Rejected } && input is StepCompleted withPatch)
            {
                // An agent's invalid patch is a contract error; in a correction turn the agent withdraws it.
                Record(new ApplyResult(true, [], result.Patch), observedChecks, patchDiagnostics, failures);
                result = core.Apply(withPatch with { Patch = null });
            }

            if (!result.Accepted)
            {
                failures.Add("the script was refused: " + string.Join("; ", result.Errors));
                break;
            }

            if (result.Patch is not null)
                Record(result, observedChecks, patchDiagnostics, failures);
        }

        // Names are checked against every revision, since a patch may add steps.
        foreach (var id in test.Script.Keys.Concat(test.Outputs.Keys).Concat(test.Expand.Keys).Concat(test.ExpectExecutions.Keys).Distinct())
            if (!core.State.IdHistory.Contains(id))
                failures.Add($"'{id}' is not a step of {test.Template} in any revision of the run");

        var path = core.State.Trace.Where(t => t.Kind == "started").Select(t => t.StepId).ToList();
        var status = core.State.Status.Name();
        foreach (var t in core.State.Trace.Where(t => t.Kind == "evaluation_failed" && test.ExpectEscalated?.Contains(t.StepId) != true))
            failures.Add($"{t.StepId}: {t.Detail}");
        if (test.ExpectCleanUp is { } expectCleanUp && !expectCleanUp.SequenceEqual(cleanUps))
            failures.Add($"clean_up commands were [{string.Join(", ", cleanUps)}], expected [{string.Join(", ", expectCleanUp)}]");

        CompareChecks(test, observedChecks, failures, patchDiagnostics);
        if (test.ExpectStatus is { } expected && expected != status)
            failures.Add($"status is {status}, expected {expected}; the run ended: {core.State.Trace.LastOrDefault()}");
        if (test.ExpectPath is { } exact && !exact.SequenceEqual(path))
            failures.Add($"path is [{string.Join(", ", path)}], expected [{string.Join(", ", exact)}]");
        if (test.ExpectPathIncludes is { } includes && Missing(includes, path) is { } missing)
            failures.Add($"path [{string.Join(", ", path)}] does not include, in order, [{string.Join(", ", includes)}]: '{missing}' not found after the entries before it");
        foreach (var (id, count) in test.ExpectExecutions)
            if (path.Count(p => p == id) is var actual && actual != count)
                failures.Add($"{id} ran {actual} time(s), expected {count}");
        var escalated = core.State.Waits.Where(w => w.Kind == WaitKind.Intervention).Select(w => w.StepId).Distinct().Order(StringComparer.Ordinal).ToList();
        if (test.ExpectEscalated is { } expectEscalated && !expectEscalated.Order(StringComparer.Ordinal).SequenceEqual(escalated))
            failures.Add($"open escalations are [{string.Join(", ", escalated)}], expected [{string.Join(", ", expectEscalated)}]");
        if (test.ExpectEscalated is null && escalated.Count > 0 && test.ExpectStatus is not "running")
            failures.Add($"unexpected open escalations: {string.Join("; ", core.State.Waits.Select(w => $"{w.StepId}: {w.Reason}"))}");

        return new WorkflowTestResult(test.Name, test.File, failures, status, path, core.State.Trace.ToList());
    }

    static void Record(ApplyResult result, SortedSet<int> observed, List<Diagnostic> diagnostics, List<string> failures)
    {
        if (!result.Accepted)
        {
            failures.Add("the patch was refused: " + string.Join("; ", result.Errors));
            return;
        }

        foreach (var d in result.Patch!.Diagnostics.Where(d => d.Blocks))
        {
            observed.Add(d.Check);
            diagnostics.Add(d);
        }
    }

    static void CompareChecks(WorkflowTest test, SortedSet<int> observed, List<string> failures, IEnumerable<Diagnostic> diagnostics)
    {
        var expected = test.ExpectValidationErrors.Distinct().Order().ToList();
        if (!expected.SequenceEqual(observed))
            failures.Add($"blocking checks are [{string.Join(", ", observed)}], expected [{string.Join(", ", expected)}]"
                         + (observed.Count > 0 ? ": " + string.Join("; ", diagnostics.Where(d => d.Blocks).Take(5)) : ""));
    }

    static string? Missing(IReadOnlyList<string> includes, IReadOnlyList<string> path)
    {
        var at = 0;
        foreach (var item in includes)
        {
            while (at < path.Count && path[at] != item)
                at++;
            if (at == path.Count)
                return item;
            at++;
        }

        return null;
    }

    static string? FirstSuccess(Step step) =>
        step.Outcomes().FirstOrDefault(o => o.Class == OutcomeClass.Success)?.Name;

    /// <summary>The visit's scripted outputs, and every other output the step publishes at its type's zero value.</summary>
    static Dictionary<string, object?> Outputs(Step step, List<YMap>? scripted, int execution, List<string> failures)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        var names = step.Outputs.Select(o => o.Name).ToList();
        if (step.Form == StepForm.Agent)
            names.AddRange(Actions.AgentEngineOutputs);
        else if (step.Action is { } a && Actions.All.GetValueOrDefault(a)?.Outputs is { } fixedOutputs)
            names.AddRange(fixedOutputs);
        foreach (var name in names.Distinct())
            outputs[name] = Zero(RunCore.OutputType(step, name), name);

        // `~` means the step does not report the output (LOG 75).
        if (scripted is { Count: > 0 })
            foreach (var (key, value) in scripted[Math.Min(execution - 1, scripted.Count - 1)].Entries)
                if (value is YScalar { IsNull: true })
                    outputs.Remove(key.Text);
                else
                    outputs[key.Text] = value.ToPlain();
        return outputs;
    }

    static object? Zero(string? type, string name) => name == "coverage_known" ? true : type switch
    {
        "number" => 0L,
        "boolean" => false,
        "string" => "",
        "array" => new List<object?>(),
        _ => null,
    };

    /// <summary>
    /// The <c>findings</c> namespace from the test file: entry k is the value at the k-th route evaluation that
    /// reads the name; the last entry repeats; an unlisted name reads 0.
    /// </summary>
    sealed class ScriptedFindings(Dictionary<string, List<long>> values) : IFindingsSource
    {
        readonly Dictionary<string, int> reads = new(StringComparer.Ordinal);

        public long Read(string name)
        {
            if (!values.TryGetValue(name, out var list) || list.Count == 0)
                return 0;
            var k = reads.GetValueOrDefault(name);
            reads[name] = k + 1;
            return list[Math.Min(k, list.Count - 1)];
        }
    }
}
