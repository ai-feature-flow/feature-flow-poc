using FeatureFlow.Expressions;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation;

/// <summary>
/// The load-phase checks of docs/09 section 2 on an expanded plan: 2 to 6, 8 to 13, 16, 17, 34 and 35.
/// Checks 1, 7, 14 and 15, and parts of 4 and 8, are found while the plan is read and expanded
/// (<see cref="PlanLoader"/>).
/// </summary>
public sealed class PlanValidator
{
    /// <summary>Spike 1 measured about 500 bytes of carried state per step; 1 MB is about 2,000 steps (docs/09 section 2.1).</summary>
    public const int MaxSteps = 2000;

    public const int BytesPerStep = 500;

    public static readonly IReadOnlyList<string> StepFields = ["outcome", "outputs", "artifacts", "previous", "executions"];

    public static readonly IReadOnlyList<string> RunFields = ["id", "level", "repositories", "branch", "parent_branch", "tracker_issue", "parent_id"];

    public static readonly IReadOnlyList<string> FindingsFields =
        ["open", "open_blocking", "open_demonstrated", "fixed", "declined", "escalated", "reraised_unchanged", "repeat_fixes"];

    /// <summary>The reserved artifact name that stages the findings ledger's current view (LOG 63).</summary>
    public const string FindingsLedger = "findings_ledger";

    readonly Plan plan;
    readonly LibrarySet libraries;
    readonly DiagnosticBag d;
    readonly Dictionary<string, Step> byId = [];
    readonly HashSet<string> usedParameters = [];

    PlanValidator(Plan plan, LibrarySet libraries, DiagnosticBag diagnostics)
    {
        this.plan = plan;
        this.libraries = libraries;
        d = diagnostics;
        foreach (var step in plan.AllSteps)
            byId.TryAdd(step.Id, step);
    }

    bool Standalone => plan.Root.Standalone;

    public static void Validate(Plan plan, LibrarySet libraries, DiagnosticBag diagnostics)
    {
        var v = new PlanValidator(plan, libraries, diagnostics);
        v.CheckIds();
        v.CheckReferences();
        v.CheckActionsAndOutcomes();
        v.CheckGuards();
        v.CheckExpressions();
        v.CheckReachability();
        v.CheckCycles();
        v.CheckArtifacts();
        v.CheckWriteAccess();
        v.CheckDecisionTypes();
        v.CheckTracker();
        v.CheckSize();
        v.CheckUnusedParameters();
    }

    // ---- Check 3 ------------------------------------------------------------------------------

    void CheckIds()
    {
        var seen = new Dictionary<string, SourceLocation>();
        foreach (var (id, location) in plan.AllSteps.Select(s => (s.Id, s.Location)).Concat(plan.Instances.Select(i => (i.Id, i.Location))))
            if (!seen.TryAdd(id, location))
                d.Add(Checks.DuplicateId, $"step id '{id}' is used more than once (first at {seen[id]})", location, id);
    }

    // ---- Checks 19 and 35, and reference types (check 1) ---------------------------------------

    void CheckReferences()
    {
        foreach (var type in plan.Document.ArtifactTypes)
            Resolve(type, "artifact_types", null);
        foreach (var instance in plan.Instances)
        {
            foreach (var type in instance.Scope.Document.ArtifactTypes)
                Resolve(type, "artifact_types", instance.Id);
            if (instance.Scope.Document.DeprecatedBy is { } replacement)
                d.Add(Checks.DeprecatedReference, $"{instance.Fragment.Text} is deprecated; use {replacement.Text}", instance.Fragment.Location, instance.Id);
        }

        foreach (var step in plan.AllSteps)
        {
            if (step.Agent is { } agent)
            {
                Resolve(agent.Role, "roles", step.Id);
                Resolve(agent.Task, "tasks", step.Id);
                foreach (var skill in agent.Skills)
                    Resolve(skill, "skills", step.Id);
                foreach (var template in agent.Planning?.Templates ?? [])
                    Resolve(template, "templates", step.Id);
            }

            if (step.Decision is { } decision)
            {
                Resolve(decision.Prompt, "prompts", step.Id);
                foreach (var file in decision.Session?.Files ?? [])
                    Resolve(file.Type, "artifact_types", step.Id);
            }

            foreach (var produced in step.Produces)
                Resolve(produced.Type, "artifact_types", step.Id);
            foreach (var consumed in step.Consumes)
                Resolve(consumed.Type, "artifact_types", step.Id);
            foreach (var route in step.Routes)
                foreach (var escalate in Escalations(route.Target))
                    Resolve(escalate.Prompt, "prompts", step.Id);
            foreach (var input in ReferenceInputs(step))
                Resolve(input.Ref, input.Type, step.Id);
        }

        foreach (var instance in plan.Instances)
            foreach (var route in instance.Scope.ExitRoutes)
                foreach (var escalate in Escalations(route.Target))
                    Resolve(escalate.Prompt, "prompts", instance.Id);
    }

    static IEnumerable<Escalate> Escalations(RouteTarget target) => target switch
    {
        Escalate e => [e],
        Guarded g => g.Entries.SelectMany(e => Escalations(e.Target)),
        _ => [],
    };

    /// <summary>Action inputs that hold library references: <c>run.child</c>'s template (also inside <c>plan.expand</c>'s step) and <c>scm.pull_request</c>'s body.</summary>
    static IEnumerable<(ItemRef Ref, string Type)> ReferenceInputs(Step step)
    {
        if (step.With is null || step.Action is null)
            yield break;
        if (step.Action == "run.child" && Literal(step.With["template"]) is { } t)
            yield return (t, "templates");
        if (step.Action == "scm.pull_request" && Literal(step.With["body"]) is { } b)
            yield return (b, "prompts");
        if (step.Action == "plan.expand" && step.With["step"] is YMap inner && inner["action"].ScalarText() == "run.child"
            && inner["with"] is YMap innerWith && Literal(innerWith["template"]) is { } it)
            yield return (it, "templates");
    }

    static ItemRef? Literal(YNode? node) => node switch
    {
        YScalar { IsNull: false } s => new ItemRef(s.Text, LibraryReference.TryParse(s.Text), s.Location),
        YUnbound u => new ItemRef(u.Parameter, null, u.Location, Unbound: true),
        _ => null,
    };

    Library.Library? Resolve(ItemRef? reference, string type, string? stepId)
    {
        if (reference is null || reference.Unbound)
            return null;
        if (reference.Reference is not { } r)
        {
            d.Add(Checks.Schema, $"'{reference.Text}' is not a library reference (<library>:<type>/<name>)", reference.Location, stepId);
            return null;
        }

        if (r.Type != type)
        {
            d.Add(Checks.Schema, $"{reference.Text} names {r.Type}, but this field takes {type}", reference.Location, stepId);
            return null;
        }

        var library = libraries.Resolve(r);
        if (library is null)
        {
            d.Add(Checks.ReferenceResolves, $"{reference.Text} does not resolve", reference.Location, stepId);
            return null;
        }

        var replacement = type switch
        {
            "roles" => library.Role(r.Name)?.DeprecatedBy,
            "artifact_types" => library.ArtifactType(r.Name)?.DeprecatedBy,
            _ => null,
        };
        if (replacement is not null)
            d.Add(Checks.DeprecatedReference, $"{reference.Text} is deprecated; use {replacement.Text}", reference.Location, stepId);
        return library;
    }

    // ---- Checks 2 and 6, and outcome names (check 1) ------------------------------------------

    void CheckActionsAndOutcomes()
    {
        foreach (var step in plan.AllSteps)
        {
            ActionContract? contract = null;
            if (step.Form == StepForm.Action && step.Action is { } action)
            {
                contract = Actions.All.GetValueOrDefault(action);
                if (contract is null)
                {
                    d.Add(Checks.UnknownAction, $"unknown action '{action}'", step.Location, step.Id);
                    continue;
                }
            }

            if (step.Form is StepForm.Agent or StepForm.Decision)
            {
                if (step.DeclaredOutcomes.Count == 0)
                {
                    d.Add(Checks.Outcomes, $"{(step.Form == StepForm.Agent ? "an agent step" : "a decision")} declares at least one outcome", step.OutcomesLocation ?? step.Location, step.Id);
                    continue;
                }

                if (step.DeclaredOutcomes.Any(o => o.Name == Actions.AttemptsExhausted))
                {
                    d.Add(Checks.Outcomes, $"'{Actions.AttemptsExhausted}' is added by the engine and may not be declared", step.OutcomesLocation ?? step.Location, step.Id);
                    continue;
                }
            }

            var names = step.Outcomes(contract).Select(o => o.Name).ToHashSet();
            void Known(string name, string what, SourceLocation location)
            {
                if (!names.Contains(name))
                    d.Add(Checks.Schema, $"{what} names '{name}', which is not an outcome of this step ({string.Join(", ", names.Order())})", location, step.Id);
            }

            foreach (var route in step.Routes)
                Known(route.Outcome, "a route", route.Location);
            foreach (var produced in step.Produces.Where(p => p.Outcome is not null))
                Known(produced.Outcome!, "produces", produced.Location);
            foreach (var outcome in step.Verdicts.Keys)
                Known(outcome, "verdicts", step.Location);
            foreach (var outcome in step.Ratifies.Keys)
                Known(outcome, "ratifies", step.Location);
            if (step.Decision?.Auto is { } auto)
                Known(auto, "decision.auto", step.Decision.TypeLocation);
            foreach (var outcome in step.Tracker?.On.Keys ?? [])
                Known(outcome, "tracker.on", step.Tracker!.Location);
        }

        foreach (var instance in plan.Instances)
        {
            var exits = instance.Scope.Document.Exits.Select(e => e.Name).ToHashSet();
            foreach (var route in instance.Scope.ExitRoutes.Where(r => !exits.Contains(r.Outcome)))
                d.Add(Checks.Fragment, $"routes '{route.Outcome}', which is not an exit of {instance.Fragment.Text} ({string.Join(", ", exits.Order())})", route.Location, instance.Id);
            foreach (var outcome in instance.Tracker?.On.Keys.Where(k => !exits.Contains(k)) ?? [])
                d.Add(Checks.Fragment, $"tracker.on names '{outcome}', which is not an exit of {instance.Fragment.Text}", instance.Tracker!.Location, instance.Id);
        }
    }

    // ---- Check 8: guarded lists ----------------------------------------------------------------

    void CheckGuards()
    {
        var routes = plan.AllSteps.SelectMany(s => s.Routes.Select(r => (r, s.Id)))
            .Concat(plan.Instances.SelectMany(i => i.Scope.ExitRoutes.Select(r => (r, i.Id))))
            .Concat(plan.Instances.Select(i => i.Scope).Append(plan.Root)
                .SelectMany(s => s.Document.Defaults.ByClass.Values.Concat(s.Document.Defaults.ByOutcome.Values)).Select(r => (r, (string)null!)));
        foreach (var (route, stepId) in routes)
            CheckGuard(route.Target, route.Outcome, stepId);
    }

    void CheckGuard(RouteTarget target, string outcome, string? stepId)
    {
        if (target is not Guarded g)
            return;
        if (g.Entries.Count == 0)
        {
            d.Add(Checks.RouteTarget, $"the guarded route for '{outcome}' is empty", g.Location, stepId);
            return;
        }

        for (var i = 0; i < g.Entries.Count - 1; i++)
            if (g.Entries[i].When is null)
                d.Add(Checks.RouteTarget, $"the guarded route for '{outcome}' has an entry without 'when' before its last entry", g.Entries[i].Target.Location, stepId);
        if (g.Entries[^1].When is not null)
            d.Add(Checks.RouteTarget, $"the guarded route for '{outcome}' does not end with a fallback (an entry without 'when')", g.Entries[^1].Target.Location, stepId);
        foreach (var entry in g.Entries)
            CheckGuard(entry.Target, outcome, stepId);
    }

    // ---- Checks 4 and 5 ------------------------------------------------------------------------

    void CheckExpressions()
    {
        foreach (var expression in plan.AllExpressions)
            CheckExpression(expression);

        foreach (var step in plan.AllSteps)
        {
            var withKeys = step.With?.Keys.ToList() ?? [];
            if (step.Agent?.Task is { Reference: { } task, Unbound: false })
                CheckText(task, "tasks", step, withKeys);
            if (step.Decision?.Prompt is { Reference: { } prompt, Unbound: false })
                CheckText(prompt, "prompts", step, withKeys);
            foreach (var input in ReferenceInputs(step).Where(i => i.Type == "prompts" && i.Ref.Reference is not null))
                CheckText(input.Ref.Reference!, "prompts", step, []);
            foreach (var route in step.Routes)
                foreach (var escalate in Escalations(route.Target))
                    if (escalate.Prompt?.Reference is { } p)
                        CheckText(p, "prompts", step, []);
        }
    }

    void CheckText(LibraryReference reference, string type, Step step, IReadOnlyList<string> withKeys)
    {
        if (reference.Type != type || libraries.Resolve(reference) is not { } library || library.Text(type, reference.Name) is not { } text)
            return;
        var file = library.Relative(library.PathOf(type, reference.Name)!);
        foreach (var e in TemplateText.Expressions(text))
            CheckExpression(new Expression(e.Text, ExpressionUse.Text, step.Scope, new SourceLocation(file, e.Line, e.Column),
                $"{reference} text", step.Id, withKeys));
    }

    void CheckExpression(Expression e)
    {
        var result = CelExpressions.Check(e.Text, e.ExtraVariables, e.Scope.HasItem);
        foreach (var error in result.Errors)
            d.Add(Checks.Expression, $"{e.Where}: '{Short(e.Text)}' {error}", e.Location, e.StepId);
        if (result.Ok && e.Use == ExpressionUse.Guard && !result.ResultIsBoolOrDyn)
            d.Add(Checks.Expression, $"{e.Where}: guard '{Short(e.Text)}' is {result.ResultType}, not bool", e.Location, e.StepId);

        foreach (var path in result.Paths)
            CheckPath(path, e);
    }

    void CheckPath(IReadOnlyList<string> path, Expression e)
    {
        switch (path[0])
        {
            case "params" when path.Count > 1:
                var doc = e.Scope.DocumentScope;
                if (doc.Document.Parameter(path[1]) is null)
                    d.Add(Checks.UndeclaredParameter, $"{e.Where}: params.{path[1]} is not a declared parameter of {doc.Document.Name}", e.Location, e.StepId);
                else
                    usedParameters.Add($"{doc.Id}|{path[1]}");
                break;
            case "run" when path.Count > 1 && !RunFields.Contains(path[1]):
                d.Add(Checks.Expression, $"{e.Where}: run.{path[1]} is not a run value ({string.Join(", ", RunFields)})", e.Location, e.StepId);
                break;
            case "findings" when path.Count > 1 && !FindingsFields.Contains(path[1]):
                d.Add(Checks.Expression, $"{e.Where}: findings.{path[1]} is not a findings count ({string.Join(", ", FindingsFields)})", e.Location, e.StepId);
                break;
            case "steps" when path.Count > 1:
                CheckStepPath(path, e);
                break;
        }
    }

    void CheckStepPath(IReadOnlyList<string> path, Expression e)
    {
        for (var k = path.Count - 1; k >= 1; k--)
        {
            var local = string.Join(".", path.Skip(1).Take(k));
            if (FindStep(local, e.Scope) is not { } target)
                continue;
            if (k + 1 >= path.Count)
                return;
            var field = path[k + 1];
            if (!StepFields.Contains(field))
            {
                d.Add(Checks.Expression, $"{e.Where}: steps.{local}.{field} is not a step value ({string.Join(", ", StepFields)})", e.Location, e.StepId);
                return;
            }

            if (k + 2 >= path.Count)
                return;
            var name = path[k + 2];
            if (field == "outputs" && Outputs(target) is { } outputs && !outputs.Contains(name))
                d.Add(Checks.Expression, $"{e.Where}: step {target.Id} has no output '{name}' ({string.Join(", ", outputs.Order())})", e.Location, e.StepId);
            if (field == "artifacts" && !target.Produces.Any(p => p.Name == name))
                d.Add(Checks.Expression, $"{e.Where}: step {target.Id} produces no artifact '{name}'", e.Location, e.StepId);
            return;
        }

        d.Add(Checks.Expression, $"{e.Where}: steps.{string.Join(".", path.Skip(1))} names no step", e.Location, e.StepId);
    }

    /// <summary>A step referenced from an expression: in the branch, then the enclosing scopes up to the document.</summary>
    Step? FindStep(string local, Scope scope)
    {
        for (var s = scope; ; s = s.Parent!)
        {
            if (byId.TryGetValue(s.Prefix + local, out var step))
                return step;
            if (s.Kind != ScopeKind.Branch)
                return null;
        }
    }

    static HashSet<string>? Outputs(Step step)
    {
        var declared = step.Outputs.Select(o => o.Name).ToHashSet();
        switch (step.Form)
        {
            case StepForm.Agent:
                declared.UnionWith(Actions.AgentEngineOutputs);
                return declared;
            case StepForm.Decision:
                return declared;
            case StepForm.Action when step.Action is { } a && Actions.All.GetValueOrDefault(a)?.Outputs is { } fixedOutputs:
                declared.UnionWith(fixedOutputs);
                return declared;
            default:
                return null;
        }
    }

    static string Short(string text) => text.Length > 80 ? text[..77] + "..." : text;

    // ---- Checks 9 and 10 -----------------------------------------------------------------------

    /// <summary>
    /// The route graph. Nodes are steps, plus one join node per container that its routes leave from.
    /// Escalations and the engine's <c>attempts_exhausted</c> default are not edges: a person decides
    /// where the run goes next, so they are not a way out of a cycle.
    /// </summary>
    sealed class Graph
    {
        public readonly List<string> Names = [];
        public readonly Dictionary<string, int> Index = [];
        public readonly List<HashSet<int>> Edges = [];
        public const int Terminal = -1;

        public int Node(string name)
        {
            if (Index.TryGetValue(name, out var i))
                return i;
            Index[name] = Names.Count;
            Names.Add(name);
            Edges.Add([]);
            return Names.Count - 1;
        }
    }

    Graph BuildGraph()
    {
        var g = new Graph();
        foreach (var step in plan.AllSteps)
        {
            g.Node(step.Id);
            if (step.Container is not null)
                g.Node(Join(step.Id));
        }

        foreach (var step in plan.AllSteps)
        {
            var from = g.Index[step.Id];
            if (step.Container is { } c)
            {
                foreach (var branch in c.Branches.Where(b => b.Steps.Count > 0))
                    g.Edges[from].Add(g.Index[branch.Steps[0].Id]);
                if (c.Branches.All(b => b.Steps.Count == 0))
                    g.Edges[from].Add(g.Index[Join(step.Id)]);
                from = g.Index[Join(step.Id)];
            }

            foreach (var route in step.ResolvedRoutes.Where(r => r.Source != RouteSource.EngineDefault))
                foreach (var to in Targets(route.Target, g))
                    g.Edges[from].Add(to);

            // A step reachable only through plan.expand's target or on_other counts as reachable (docs/09 section 2.2).
            if (step.Action == "plan.expand" && step.With is { } with)
                foreach (var key in new[] { "target", "on_other" })
                    if (with[key].ScalarText() is { } local && FindStep(local, step.Scope) is { } target)
                        g.Edges[g.Index[step.Id]].Add(g.Index[target.Id]);
        }

        return g;
    }

    static string Join(string containerId) => containerId + " (join)";

    static IEnumerable<int> Targets(Resolved target, Graph g) => target switch
    {
        ResolvedStep s when g.Index.TryGetValue(s.StepId, out var i) => [i],
        ResolvedBranchEnd b when g.Index.TryGetValue(Join(b.ContainerId), out var j) => [j],
        // In a fragment validated alone, a route left to the caller's defaults is not known to leave a cycle.
        ResolvedExit { Exit: PlanLoader.CallerDefault } => [],
        ResolvedEnd or ResolvedExit => [Graph.Terminal],
        ResolvedGuarded guarded => guarded.Entries.SelectMany(e => Targets(e.Target, g)),
        _ => [],
    };

    void CheckReachability()
    {
        if (plan.Steps.Count == 0)
            return;
        var g = BuildGraph();
        var seen = new HashSet<int> { g.Index[plan.Steps[0].Id] };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var next in g.Edges[queue.Dequeue()].Where(n => n >= 0 && seen.Add(n)))
                queue.Enqueue(next);

        foreach (var step in plan.AllSteps.Where(s => !seen.Contains(g.Index[s.Id])))
            d.Add(Checks.Reachable, "no route reaches this step from the first step", step.Location, step.Id);
    }

    void CheckCycles()
    {
        var g = BuildGraph();
        foreach (var component in StronglyConnected(g))
        {
            var cyclic = component.Count > 1 || g.Edges[component[0]].Contains(component[0]);
            if (!cyclic)
                continue;
            var members = component.ToHashSet();
            if (component.Any(n => g.Edges[n].Any(t => !members.Contains(t))))
                continue;
            var steps = component.Select(n => g.Names[n]).Where(byId.ContainsKey).OrderBy(id => Order(id)).ToList();
            var first = byId[steps[0]];
            d.Add(Checks.CycleExit,
                $"the cycle {string.Join(" -> ", steps)} has no route out: every outcome stays in it or escalates",
                first.Location, first.Id);
        }
    }

    int Order(string id) => plan.AllSteps.Select((s, i) => (s, i)).First(x => x.s.Id == id).i;

    /// <summary>Tarjan's algorithm, iterative.</summary>
    static List<List<int>> StronglyConnected(Graph g)
    {
        var n = g.Names.Count;
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var result = new List<List<int>>();
        var counter = 0;
        for (var start = 0; start < n; start++)
        {
            if (index[start] >= 0)
                continue;
            var work = new Stack<(int Node, IEnumerator<int> Next)>();
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack[start] = true;
            work.Push((start, g.Edges[start].Where(t => t >= 0).GetEnumerator()));
            while (work.Count > 0)
            {
                var (v, it) = work.Peek();
                if (it.MoveNext())
                {
                    var w = it.Current;
                    if (index[w] < 0)
                    {
                        index[w] = low[w] = counter++;
                        stack.Push(w);
                        onStack[w] = true;
                        work.Push((w, g.Edges[w].Where(t => t >= 0).GetEnumerator()));
                    }
                    else if (onStack[w])
                        low[v] = Math.Min(low[v], index[w]);
                }
                else
                {
                    work.Pop();
                    if (work.Count > 0)
                        low[work.Peek().Node] = Math.Min(low[work.Peek().Node], low[v]);
                    if (low[v] == index[v])
                    {
                        var component = new List<int>();
                        int w;
                        do
                        {
                            w = stack.Pop();
                            onStack[w] = false;
                            component.Add(w);
                        } while (w != v);
                        result.Add(component);
                    }
                }
            }
        }

        return result;
    }

    // ---- Checks 11 and 12 ----------------------------------------------------------------------

    void CheckArtifacts()
    {
        var allowed = AllowedTypes();
        var producers = plan.AllSteps.SelectMany(s => s.Produces.Select(p => (Step: s, Out: p))).ToList();

        foreach (var (step, produced) in producers)
        {
            if (produced.Type is not { Reference: { } typeRef } type)
                continue;
            if (!allowed.TryGetValue(step.Scope.DocumentScope.Id, out var set) || !set.Contains(Key(typeRef)))
                d.Add(Checks.ArtifactProducer,
                    $"produces '{produced.Name}' as {type.Text}, which neither {step.Scope.DocumentScope.Document.Name} nor a fragment it uses lists in artifact_types",
                    produced.Location, step.Id);
            if (step.Form is StepForm.Agent or StepForm.Decision && ArtifactType(typeRef)?.Evidence == true)
                d.Add(Checks.EvidenceType,
                    $"{(step.Form == StepForm.Agent ? "an agent step" : "a decision")} may not produce '{produced.Name}' of evidence type {type.Text}; only engine actions produce evidence (F7)",
                    produced.Location, step.Id);
        }

        foreach (var step in plan.AllSteps)
            foreach (var file in step.Decision?.Session?.Files ?? [])
                if (file.Type?.Reference is { } r && ArtifactType(r)?.Evidence == true)
                    d.Add(Checks.EvidenceType, $"a session may not produce '{file.Name}' of evidence type {file.Type.Text} (F7)", step.Decision!.Session!.Location, step.Id);

        // Producers of one name that are visible to each other must agree on its type.
        foreach (var group in producers.Where(p => p.Out.Type?.Reference is not null).GroupBy(p => (Branch(p.Step), p.Out.Name)))
        {
            var types = group.Select(p => p.Out.Type!.Reference!).DistinctBy(Key).ToList();
            for (var i = 1; i < types.Count; i++)
                if (!Compatible(types[i], types[0]) && !Compatible(types[0], types[i]))
                {
                    var (step, produced) = group.First(p => Key(p.Out.Type!.Reference!) == Key(types[i]));
                    d.Add(Checks.ArtifactProducer,
                        $"produces '{produced.Name}' as {types[i]}, but another step produces it as {types[0]}, and neither type is compatible with the other",
                        produced.Location, step.Id);
                }
        }

        if (Standalone)
            return;

        foreach (var step in plan.AllSteps)
        {
            var consumed = step.Consumes.Select(c => c.Name).ToHashSet();
            foreach (var input in step.Consumes)
            {
                if (input.From.StartsWith("child:", StringComparison.Ordinal))
                {
                    if (FindStep(input.From["child:".Length..], step.Scope) is null)
                        d.Add(Checks.ArtifactProducer, $"consumes '{input.Name}' from child '{input.From[6..]}', which is not a step", input.Location, step.Id);
                    continue;
                }

                if (input.From != "current" || input.Optional || input.Name == FindingsLedger)
                    continue;
                var visible = producers.Where(p => p.Out.Name == input.Name && Visible(p.Step, step)).ToList();
                if (visible.Count == 0)
                {
                    d.Add(Checks.ArtifactProducer, $"consumes '{input.Name}', which no step produces; mark it optional or produce it", input.Location, step.Id);
                    continue;
                }

                if (input.Type?.Reference is { } wanted)
                    foreach (var (producer, produced) in visible)
                        if (produced.Type?.Reference is { } given && !Compatible(given, wanted))
                            d.Add(Checks.ArtifactProducer,
                                $"consumes '{input.Name}' as {wanted}, but step {producer.Id} produces {given}, which is not compatible",
                                input.Location, step.Id);
            }

            foreach (var (name, location) in step.FromArtifacts.Where(f => !consumed.Contains(f.Name)))
                d.Add(Checks.ArtifactProducer, $"binds {{ from_artifact: {name} }}, which the step does not consume", location, step.Id);
            foreach (var name in step.Verdicts.Values.SelectMany(v => v.Accept.Concat(v.Reject)).Concat(step.Ratifies.Values.SelectMany(v => v)).Distinct())
                if (!consumed.Contains(name))
                    d.Add(Checks.ArtifactProducer, $"gives a verdict on or ratifies '{name}', which the step does not consume (docs/03 section 7.4)", step.Location, step.Id);
        }
    }

    /// <summary>Artifact types a step may use: its document's, and those of every fragment the document uses, transitively.</summary>
    Dictionary<string, HashSet<string>> AllowedTypes()
    {
        var scopes = plan.Instances.Select(i => i.Scope).Append(plan.Root).ToList();
        var result = new Dictionary<string, HashSet<string>>();
        HashSet<string> For(Scope s)
        {
            if (result.TryGetValue(s.Id, out var set))
                return set;
            set = s.Document.ArtifactTypes.Where(t => t.Reference is not null).Select(t => Key(t.Reference!)).ToHashSet();
            result[s.Id] = set;
            foreach (var child in scopes.Where(c => c.Parent?.DocumentScope == s))
                set.UnionWith(For(child));
            return set;
        }

        foreach (var s in scopes)
            For(s);
        return result;
    }

    static string Key(LibraryReference r) => $"{r.Library}:{r.Type}/{r.Name}";

    ArtifactTypeItem? ArtifactType(LibraryReference r) =>
        r.Type == "artifact_types" ? libraries.Resolve(r)?.ArtifactType(r.Name) : null;

    /// <summary>The same type, or <paramref name="given"/> declares <c>compatible_with</c> <paramref name="wanted"/> (docs/03 section 7.3).</summary>
    bool Compatible(LibraryReference given, LibraryReference wanted) =>
        Key(given) == Key(wanted)
        || (ArtifactType(given)?.CompatibleWith.Any(c => c.Reference is { } cr && Key(cr) == Key(wanted)) ?? false);

    /// <summary>The innermost <c>for_each</c> branch a step is in, or "" (names produced in a branch are scoped to it, LOG 62).</summary>
    static string Branch(Step step) => Branches(step).FirstOrDefault() ?? "";

    static IEnumerable<string> Branches(Step step)
    {
        for (var s = step.Scope; s is not null; s = s.Parent)
            if (s.Kind == ScopeKind.Branch && s.IsForEach)
                yield return s.Id;
    }

    static bool Visible(Step producer, Step consumer)
    {
        var branch = Branch(producer);
        return branch.Length == 0 || Branches(consumer).Contains(branch);
    }

    // ---- Check 13 ------------------------------------------------------------------------------

    void CheckWriteAccess()
    {
        foreach (var step in plan.AllSteps)
        {
            if (step.Agent is not { } agent)
                continue;
            if (agent.ToolsLocation is { } tools)
                d.Add(Checks.WriteAccess, "a step may not set tools or toolsets; they come from the role (F10)", tools, step.Id);
            if (agent.Role?.Reference is not { } roleRef || roleRef.Type != "roles" || libraries.Resolve(roleRef)?.Role(roleRef.Name) is not { } role)
                continue;
            step.EffectiveWriteAccess = agent.WriteAccess ?? role.WriteAccess;
            if (agent.WriteAccess is not { } access)
                continue;
            if (WriteAccess.Rank(access) > WriteAccess.Rank(role.WriteAccess))
                d.Add(Checks.WriteAccess,
                    $"write_access '{access}' exceeds role {roleRef}'s '{role.WriteAccess}'; a step may only tighten its role's access (F10)",
                    step.Location, step.Id);
        }
    }

    // ---- Check 16 ------------------------------------------------------------------------------

    void CheckDecisionTypes()
    {
        var declared = libraries.DecisionTypes.ToList();
        void Check(string type, SourceLocation location, string? stepId, string what)
        {
            if (!declared.Any(t => t.Matches(type)))
                d.Add(Checks.DecisionType, $"{what} '{type}' is not declared by any library in the run (docs/05 section 4)", location, stepId);
        }

        foreach (var step in plan.AllSteps)
        {
            if (step.Decision is { Type: { } type } decision)
                Check(type, decision.TypeLocation, step.Id, "decision type");
            foreach (var route in step.Routes)
                foreach (var escalate in Escalations(route.Target).Where(e => e.DecisionType is not null))
                    Check(escalate.DecisionType!, escalate.Location, step.Id, "escalation type");
        }

        if (Standalone || plan.Document.Level is not { } level)
            return;
        var raised = new List<(string Suffix, string Why)>();
        if (plan.AllSteps.Any(s => s.ResolvedRoutes.Any(r => Raises(r.Target))))
            raised.Add(("escalation", "an escalate route"));
        if (plan.AllSteps.Any(s => s.Limits?.QuestionsMax is not null || s.Limits?.QuestionsTimeout is not null))
            raised.Add(("question", "an agent step with limits.questions"));
        if (plan.AllSteps.Any(s => s.Action == "scm.merge"))
            raised.Add(("merge_action", "an scm.merge step"));
        if (plan.AllSteps.Any(s => s.Agent?.Planning is not null))
            raised.Add(("patch_approval", "an agent step with planning"));
        foreach (var (suffix, why) in raised)
            Check($"{level}.{suffix}", plan.Document.ExitsLocation ?? plan.Steps[0].Location, null, $"engine-raised type for {why}");
    }

    static bool Raises(Resolved target) => target switch
    {
        ResolvedEscalate e => e.DecisionType is null,
        ResolvedGuarded g => g.Entries.Any(x => Raises(x.Target)),
        _ => false,
    };

    // ---- Tracker statuses (check 1) ------------------------------------------------------------

    void CheckTracker()
    {
        var statuses = libraries.Statuses.Select(s => s.Name).ToHashSet();
        var trackers = plan.AllSteps.Where(s => s.Tracker is not null).Select(s => (s.Tracker!, s.Id))
            .Concat(plan.Instances.Where(i => i.Tracker is not null).Select(i => (i.Tracker!, i.Id)));
        foreach (var (tracker, id) in trackers)
            foreach (var status in tracker.On.Values.Prepend(tracker.OnStart).OfType<string>())
                if (!statuses.Contains(status))
                    d.Add(Checks.Schema, $"tracker status '{status}' is not in the library's status map ({string.Join(", ", statuses.Order())})", tracker.Location, id);
    }

    // ---- Check 17 ------------------------------------------------------------------------------

    void CheckSize()
    {
        var count = plan.AllSteps.Count();
        if (count > MaxSteps)
            d.Add(Checks.PlanSize,
                $"the plan has {count} steps, about {count * BytesPerStep / 1024} KB of carried state; the limit is {MaxSteps} steps (1 MB). Split the work into child runs",
                new SourceLocation(plan.Document.File, 1, 1));
    }

    // ---- Check 34 ------------------------------------------------------------------------------

    void CheckUnusedParameters()
    {
        foreach (var e in plan.AllExpressions)
            foreach (var p in CelExpressions.Check(e.Text, e.ExtraVariables, e.Scope.HasItem).Paths.Where(p => p.Count > 1 && p[0] == "params"))
                usedParameters.Add($"{e.Scope.DocumentScope.Id}|{p[1]}");
        foreach (var p in plan.SubstitutedParameters)
            usedParameters.Add(p);
        foreach (var p in plan.Document.Parameters)
            if (!usedParameters.Contains($"{plan.Root.Id}|{p.Name}"))
                d.Add(Checks.UnusedParameter, $"parameter '{p.Name}' is declared but never used", p.Location);
    }
}
