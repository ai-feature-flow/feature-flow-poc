using System.Text.RegularExpressions;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Library;

/// <summary>Values known when a template is loaded: bound parameters and the run's repositories.</summary>
public sealed record LoadOptions(IReadOnlyDictionary<string, YNode>? Parameters = null);

/// <summary>
/// Loads a template into an expanded <see cref="Plan"/>: fragments replaced by their steps with ids
/// prefixed (docs/03 section 11; LOG 62), <c>for_each</c> expanded into a <c>parallel</c> or a chain
/// (section 8.2), and every outcome's route resolved after defaults (section 4.3).
/// </summary>
public sealed partial class PlanLoader
{
    /// <summary>Fragments may nest to a depth of 3 (docs/03 section 11).</summary>
    public const int MaxFragmentDepth = 3;

    /// <summary>The target of a classed outcome in a fragment validated alone, which the caller's defaults would route.</summary>
    public const string CallerDefault = "(caller default)";

    readonly LibrarySet libraries;
    readonly DiagnosticBag d;
    readonly LoadOptions options;
    Plan plan = null!;

    /// <summary>Each lane (the top level, or one branch) in order, for "next step" routing.</summary>
    readonly Dictionary<string, List<Step>> lanes = [];

    /// <summary>Each fragment instance's lane and its first and last positions in it.</summary>
    readonly Dictionary<string, (string Lane, int First, int Last)> instanceSpans = [];

    readonly Dictionary<string, Step> stepsById = [];

    readonly HashSet<string> substituted = [];

    PlanLoader(LibrarySet libraries, DiagnosticBag diagnostics, LoadOptions options)
    {
        this.libraries = libraries;
        d = diagnostics;
        this.options = options;
    }

    [GeneratedRegex(@"^params\.([a-z][a-z0-9_]*)$")]
    private static partial Regex ParamPath();

    public static Plan? LoadTemplate(LibrarySet libraries, LibraryReference reference, DiagnosticBag diagnostics, LoadOptions? options = null) =>
        new PlanLoader(libraries, diagnostics, options ?? new LoadOptions()).Load(reference, "template");

    /// <summary>
    /// Loads a fragment on its own, with no caller: literal parameter defaults are substituted, other
    /// <c>{ param: }</c> fields stay unbound, and exits are left unrouted.
    /// </summary>
    public static Plan? LoadFragment(LibrarySet libraries, LibraryReference reference, DiagnosticBag diagnostics) =>
        new PlanLoader(libraries, diagnostics, new LoadOptions()).Load(reference, "fragment");

    Plan? Load(LibraryReference reference, string kind)
    {
        var library = libraries.Resolve(reference);
        if (library is null)
        {
            d.Add(Checks.ReferenceResolves, $"{reference} does not resolve");
            return null;
        }

        var raw = library.Yaml(reference.Type, reference.Name);
        d.AddRange(library.Diagnostics.Where(x => x.Location?.File == library.Relative(library.PathOf(reference.Type, reference.Name)!)));
        if (raw is null)
            return null;

        var file = library.Relative(library.PathOf(reference.Type, reference.Name)!);
        YNode root = raw;
        if (kind == "fragment")
        {
            var decls = ReadParameterDecls(raw);
            root = Substitute(raw, name => decls.GetValueOrDefault(name) switch
            {
                null => SubstitutionResult.Undeclared,
                { Default: { } dflt } when !dflt.IsSingleKeyMap("expr", out _) => new SubstitutionResult(dflt, false),
                _ => SubstitutionResult.Unbound,
            }, "", null);
        }

        var read = DocumentReader.Read(root, kind, reference.Name, file, reference, d);
        if (read is null)
            return null;

        var scope = new Scope
        {
            Id = "",
            Prefix = "",
            Kind = kind == "template" ? ScopeKind.Template : ScopeKind.Fragment,
            Document = read.Info,
            Depth = kind == "template" ? 0 : 1,
            Standalone = kind == "fragment",
        };
        read.Info.Defaults = DocumentReader.ReadDefaults(read.Defaults, scope, d);
        plan = new Plan { Document = read.Info, Root = scope };
        CollectDefaultGuards(read.Info.Defaults);
        CollectDocumentExpressions(read.Info, scope);

        lanes[""] = plan.Steps;
        if (read.Steps is not null)
            Expand(read.Steps, scope, plan.Steps, "");
        if (plan.Steps.Count == 0)
            d.Add(Checks.Schema, "the document has no steps", read.Steps?.Location ?? new SourceLocation(file, 1, 1));

        foreach (var instance in plan.Instances)
            if (instanceSpans.TryGetValue(instance.Id, out var span) && span.Last >= span.First)
                instance.FirstStepId = lanes[span.Lane][span.First].Id;

        ResolveRoutes();
        plan.SubstitutedParameters.UnionWith(substituted);
        return plan;
    }

    // ---- Expansion ----------------------------------------------------------------------------

    void Expand(YSeq entries, Scope scope, List<Step> lane, string laneId)
    {
        foreach (var entry in entries.Items)
            ExpandEntry(entry, scope, lane, laneId, idOverride: null);
    }

    void ExpandEntry(YNode entry, Scope scope, List<Step> lane, string laneId, string? idOverride)
    {
        if (entry is not YMap map)
        {
            d.Add(Checks.Schema, $"a step must be a map, not {entry.Describe()}", entry.Location);
            return;
        }

        var forms = StepReader.FormKeys.Where(map.Has).ToList();
        if (forms.Count == 0)
        {
            d.Add(Checks.UnknownAction,
                $"unknown construct with keys {string.Join(", ", map.Keys)}: a step needs one of {string.Join(", ", StepReader.FormKeys)}",
                map.Location, map["id"].ScalarText());
            return;
        }

        var r = new MapReader(map, d, "a step", map["id"].ScalarText());
        if (forms.Count > 1)
        {
            r.Error($"a step has exactly one of agent, decision, action, parallel, for_each or use; this one has {string.Join(", ", forms)}", map.Location);
            return;
        }

        string id;
        if (idOverride is not null)
        {
            id = idOverride;
            if (map.Has("id"))
                r.Error("a for_each step takes its id from the for_each; remove this id", map["id"]!.Location);
            r.Node("id");
        }
        else
        {
            var written = r.String("id", required: true);
            if (written is null)
                return;
            if (written.Contains('.'))
                r.Error($"step id '{written}' contains '.', which is reserved for fragment ids", map["id"]!.Location);
            else if (!StepReader.SnakeCase().IsMatch(written))
                r.Error($"step id '{written}' must be snake_case", map["id"]!.Location);
            id = written;
        }

        var fullId = scope.Prefix + id;
        switch (forms[0])
        {
            case "use":
                ExpandUse(r, id, fullId, scope, lane, laneId, branchRoot: false);
                break;
            case "for_each":
                ExpandForEach(r, id, fullId, scope, lane, laneId);
                break;
            case "parallel":
                ExpandParallel(r, id, fullId, scope, lane, laneId);
                break;
            default:
                var form = forms[0] switch
                {
                    "agent" => StepForm.Agent,
                    "decision" => StepForm.Decision,
                    _ => StepForm.Action,
                };
                var step = StepReader.Read(r, fullId, id, form, scope, d);
                r.Finish();
                Add(step, lane, laneId);
                break;
        }
    }

    void Add(Step step, List<Step> lane, string laneId)
    {
        step.Lane = laneId;
        step.LaneIndex = lane.Count;
        lane.Add(step);
        stepsById.TryAdd(step.Id, step);
    }

    void ExpandUse(MapReader r, string id, string fullId, Scope scope, List<Step> lane, string laneId, bool branchRoot)
    {
        var reference = LibraryItemReader.Ref(r.Node("use"), r, "use");
        var bindings = new Dictionary<string, YNode>();
        var withNode = r.Node("with");
        if (withNode is YMap withMap)
            foreach (var (key, value) in withMap.Entries)
                bindings[key.Text] = value;
        else if (withNode is not null and not YScalar { IsNull: true })
            r.Error("with must be a map", withNode.Location);

        var routes = new List<RouteSpec>();
        var routesNode = r.Node("routes");
        if (routesNode is not null && branchRoot)
            d.Add(Checks.ParallelRoutes, "a branch has no routes: its outcome's class is reported to the join (docs/03 section 8.1)", routesNode.Location, fullId);
        else if (MapReader.Of(routesNode, d, "routes", fullId) is { } rr)
            foreach (var (key, value) in rr.Map.Entries)
                if (StepReader.ReadTarget(value, scope, d, fullId, $"route {key.Text}") is { } target)
                    routes.Add(new RouteSpec(key.Text, target, scope, key.Location));

        foreach (var route in routes)
            plan.OtherExpressions.AddRange(StepReader.Guards(route.Target));

        var isProtected = r.Bool("protected") ?? false;
        var tracker = StepReader.ReadTracker(r.Node("tracker"), d, fullId);
        r.String("name");
        r.Finish();
        if (reference is null || reference.Reference is null)
            return;
        if (reference.Reference.Type != "fragments")
        {
            d.Add(Checks.Schema, $"use names a fragment, not {reference.Text}", reference.Location, fullId);
            return;
        }

        var library = libraries.Resolve(reference.Reference);
        if (library is null)
        {
            d.Add(Checks.ReferenceResolves, $"fragment {reference.Text} does not resolve", reference.Location, fullId);
            return;
        }

        var depth = scope.DocumentScope.Depth + 1;
        if (depth > MaxFragmentDepth)
        {
            d.Add(Checks.Fragment, $"fragment {reference.Text} is nested {depth} deep; fragments nest at most {MaxFragmentDepth} deep", reference.Location, fullId);
            return;
        }

        foreach (var (key, value) in bindings)
            CollectExpressions(value, scope, $"use {fullId}, with.{key}", fullId, plan.OtherExpressions);

        var raw = library.Yaml("fragments", reference.Reference.Name);
        var file = library.Relative(library.PathOf("fragments", reference.Reference.Name)!);
        d.AddRange(library.Diagnostics.Where(x => x.Location?.File == file));
        if (raw is null)
            return;

        var decls = ReadParameterDecls(raw);
        foreach (var (key, value) in bindings)
            if (!decls.ContainsKey(key))
                d.Add(Checks.Fragment, $"binds '{key}', which fragment {reference.Text} does not declare", value.Location, fullId);
        foreach (var decl in decls.Values)
            if (decl.Required && decl.Default is null && !bindings.ContainsKey(decl.Name))
                d.Add(Checks.Fragment, $"required parameter '{decl.Name}' of fragment {reference.Text} is not bound", r.Map.Location, fullId);

        var substituted = Substitute(raw, name =>
        {
            if (!decls.TryGetValue(name, out var decl))
                return SubstitutionResult.Undeclared;
            if (bindings.TryGetValue(name, out var bound))
                return new SubstitutionResult(bound, bound.IsSingleKeyMap("expr", out _));
            if (decl.Default is { } dflt)
                return new SubstitutionResult(dflt, dflt.IsSingleKeyMap("expr", out _));
            return SubstitutionResult.Unbound;
        }, fullId, fullId);

        var read = DocumentReader.Read(substituted, "fragment", reference.Reference.Name, file, reference.Reference, d);
        if (read is null)
            return;

        var fragmentScope = new Scope
        {
            Id = fullId,
            Prefix = fullId + ".",
            Kind = ScopeKind.Fragment,
            Parent = scope,
            Document = read.Info,
            Bindings = bindings,
            UseId = fullId,
            ExitRoutes = routes,
            UseLocation = r.Map.Location,
            Depth = depth,
            BranchRoot = branchRoot,
        };
        read.Info.Defaults = DocumentReader.ReadDefaults(read.Defaults, fragmentScope, d);
        CollectDefaultGuards(read.Info.Defaults);
        CollectDocumentExpressions(read.Info, fragmentScope);
        var instance = new FragmentInstance(fullId, reference, fragmentScope, isProtected, tracker, r.Map.Location);
        plan.Instances.Add(instance);

        var first = lane.Count;
        if (read.Steps is not null)
            Expand(read.Steps, fragmentScope, lane, laneId);
        instanceSpans[fullId] = (laneId, first, lane.Count - 1);
        if (isProtected)
            foreach (var step in Walk(lane.Skip(first)))
                step.Protected = true;
    }

    static IEnumerable<Step> Walk(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Container is { } c)
                foreach (var inner in Walk(c.Branches.SelectMany(b => b.Steps)))
                    yield return inner;
        }
    }

    void ExpandForEach(MapReader r, string id, string fullId, Scope scope, List<Step> lane, string laneId)
    {
        var fe = MapReader.Of(r.Node("for_each"), d, "for_each", fullId);
        if (fe is null)
            return;
        var itemsNode = fe.Required("items");
        var mode = fe.Enum("mode", ["parallel", "chain"]) ?? "parallel";
        var maxConcurrency = fe.Int("max_concurrency");
        var stepNode = fe.Required("step");
        fe.Finish();

        var container = StepReader.Read(r, fullId, id, mode == "chain" ? StepForm.Chain : StepForm.Parallel, scope, d);
        r.Finish();

        Expression? items = null;
        IReadOnlyList<YNode>? known = null;
        switch (itemsNode)
        {
            case YSeq literal:
                known = literal.Items;
                break;
            case YMap m when m.IsSingleKeyMap("expr", out var e) && e is YScalar s:
                items = new Expression(s.Text, ExpressionUse.Items, scope, s.Location, "for_each.items", fullId);
                container.Expressions.Add(items);
                known = (StaticValue(s.Text, scope) as YSeq)?.Items;
                break;
            case null:
                break;
            default:
                d.Add(Checks.Schema, "for_each.items must be a list or { expr: <cel> }", itemsNode.Location, fullId);
                break;
        }

        container.Container = new Container
        {
            Mode = mode,
            MaxConcurrency = maxConcurrency,
            FromForEach = true,
            Items = items,
            ItemsKnown = known is not null,
        };
        Add(container, lane, laneId);
        if (stepNode is null)
            return;

        // A list known only at run time is shown with one representative branch.
        var count = known?.Count ?? 1;
        for (var i = 0; i < count; i++)
        {
            var branchId = $"{id}_{i}";
            var branchScope = new Scope
            {
                Id = $"{fullId}_{i}",
                Prefix = $"{scope.Prefix}{branchId}.",
                Kind = ScopeKind.Branch,
                Parent = scope,
                Document = scope.Document,
                IsForEach = true,
                Item = known?[i],
                BranchIndex = i,
                ContainerId = fullId,
            };
            var branch = new Branch { Index = i, Scope = branchScope };
            container.Container.Branches.Add(branch);
            var branchLane = $"{fullId}#{i}";
            lanes[branchLane] = branch.Steps;
            switch (stepNode)
            {
                case YSeq list:
                    Expand(list, branchScope, branch.Steps, branchLane);
                    break;
                case YMap single when single.Has("use"):
                    ExpandBranchUse(single, branchId, branchScope, branch.Steps, branchLane);
                    break;
                case YMap single:
                    ExpandBranchStep(single, branchId, branchScope, branch.Steps, branchLane);
                    break;
                default:
                    d.Add(Checks.Schema, "for_each.step must be a step, a use of a fragment, or a list of steps", stepNode.Location, fullId);
                    break;
            }
        }
    }

    /// <summary>A branch made of one step: its id is the branch id, and it has no routes.</summary>
    void ExpandBranchStep(YMap map, string branchId, Scope branchScope, List<Step> lane, string laneId)
    {
        var flatScope = new Scope
        {
            Id = branchScope.Id,
            Prefix = branchScope.Parent!.Prefix,
            Kind = ScopeKind.Branch,
            Parent = branchScope.Parent,
            Document = branchScope.Document,
            IsForEach = branchScope.IsForEach,
            Item = branchScope.Item,
            BranchIndex = branchScope.BranchIndex,
            ContainerId = branchScope.ContainerId,
        };
        if (map.Has("routes"))
            d.Add(Checks.ParallelRoutes, "a branch has no routes: its outcome's class is reported to the join (docs/03 section 8.1)",
                map["routes"]!.Location, flatScope.Prefix + branchId);
        var stripped = new YMap(map.Entries.Where(e => e.Key.Text != "routes").ToList(), map.Location);
        ExpandEntry(stripped, flatScope, lane, laneId, branchId);
    }

    void ExpandBranchUse(YMap map, string branchId, Scope branchScope, List<Step> lane, string laneId)
    {
        var r = new MapReader(map, d, "a for_each step", branchScope.Parent!.Prefix + branchId);
        if (map.Has("id"))
            r.Error("a for_each step takes its id from the for_each; remove this id", map["id"]!.Location);
        r.Node("id");
        var flatScope = new Scope
        {
            Id = branchScope.Id,
            Prefix = branchScope.Parent!.Prefix,
            Kind = ScopeKind.Branch,
            Parent = branchScope.Parent,
            Document = branchScope.Document,
            IsForEach = branchScope.IsForEach,
            Item = branchScope.Item,
            BranchIndex = branchScope.BranchIndex,
            ContainerId = branchScope.ContainerId,
        };
        ExpandUse(r, branchId, flatScope.Prefix + branchId, flatScope, lane, laneId, branchRoot: true);
    }

    void ExpandParallel(MapReader r, string id, string fullId, Scope scope, List<Step> lane, string laneId)
    {
        var p = MapReader.Of(r.Node("parallel"), d, "parallel", fullId);
        if (p is null)
            return;
        var branchesNode = p.Node("branches");
        var joinNode = p.Node("join");
        var join = "all";
        if (joinNode is YScalar { IsNull: false } js)
        {
            if (js.Text is "all" or "any" || js.AsLong() is > 0)
                join = js.Text;
            else
                p.Error("join must be all, any or a positive number", js.Location);
        }

        var maxConcurrency = p.Int("max_concurrency");
        p.Finish();
        var container = StepReader.Read(r, fullId, id, StepForm.Parallel, scope, d);
        r.Finish();
        container.Container = new Container { Mode = "parallel", Join = join, MaxConcurrency = maxConcurrency };
        Add(container, lane, laneId);

        var entries = LibraryItemReader.Seq(branchesNode, d, "parallel.branches");
        for (var i = 0; i < entries.Count; i++)
        {
            var branchScope = new Scope
            {
                Id = $"{fullId}#{i}",
                Prefix = scope.Prefix,
                Kind = ScopeKind.Branch,
                Parent = scope,
                Document = scope.Document,
                BranchIndex = i,
                ContainerId = fullId,
            };
            var branch = new Branch { Index = i, Scope = branchScope };
            container.Container.Branches.Add(branch);
            var branchLane = $"{fullId}#{i}";
            lanes[branchLane] = branch.Steps;
            if (entries[i] is YMap m && m.Has("routes"))
            {
                d.Add(Checks.ParallelRoutes, "a branch has no routes: its outcome's class is reported to the join (docs/03 section 8.1)",
                    m["routes"]!.Location, scope.Prefix + m["id"].ScalarText());
                ExpandEntry(new YMap(m.Entries.Where(e => e.Key.Text != "routes").ToList(), m.Location), branchScope, branch.Steps, branchLane, null);
            }
            else
                ExpandEntry(entries[i], branchScope, branch.Steps, branchLane, null);
        }
    }

    // ---- Fragment parameters ------------------------------------------------------------------

    sealed record SubstitutionResult(YNode? Value, bool IsExpression)
    {
        public static readonly SubstitutionResult Undeclared = new(null, false);
        public static readonly SubstitutionResult Unbound = new(null, false) { NoValue = true };
        public bool NoValue { get; init; }
    }

    Dictionary<string, ParameterDecl> ReadParameterDecls(YNode raw)
    {
        var result = new Dictionary<string, ParameterDecl>();
        if (raw is YMap map && map["parameters"] is YMap ps)
            foreach (var (key, value) in ps.Entries)
            {
                var m = value as YMap;
                var required = m?["required"] is YScalar rs && rs.AsBool() == true;
                result[key.Text] = new ParameterDecl(key.Text, m?["type"].ScalarText() ?? "string", required, m?["default"], null, key.Location);
            }

        return result;
    }

    /// <summary>
    /// Replaces each <c>{ param: name }</c> with the parameter's literal value (LOG 62). The
    /// <c>parameters</c> section itself is left alone.
    /// </summary>
    YNode Substitute(YNode raw, Func<string, SubstitutionResult> lookup, string scopeId, string? useId)
    {
        YNode Visit(YNode node, bool top)
        {
            switch (node)
            {
                case YMap m when m.IsSingleKeyMap("param", out var nameNode):
                    var name = nameNode.ScalarText() ?? "";
                    var result = lookup(name);
                    substituted.Add($"{scopeId}|{name}");
                    if (result == SubstitutionResult.Undeclared)
                    {
                        d.Add(Checks.UndeclaredParameter, $"{{ param: {name} }} names a parameter the fragment does not declare", m.Location, useId);
                        return new YUnbound(name, m.Location);
                    }

                    if (result.NoValue || result.Value is null)
                    {
                        if (useId is not null)
                            d.Add(Checks.Fragment, $"{{ param: {name} }} has no value: bind '{name}' to a literal", m.Location, useId);
                        return new YUnbound(name, m.Location);
                    }

                    if (result.IsExpression)
                    {
                        d.Add(Checks.Fragment, $"{{ param: {name} }} needs a literal value, but '{name}' is bound to an expression (LOG 62)", m.Location, useId);
                        return new YUnbound(name, m.Location);
                    }

                    return result.Value;
                case YMap m:
                    return new YMap(m.Entries.Select(e =>
                        top && e.Key.Text == "parameters" ? e : new KeyValuePair<YScalar, YNode>(e.Key, Visit(e.Value, false))).ToList(), m.Location);
                case YSeq q:
                    return new YSeq(q.Items.Select(i => Visit(i, false)).ToList(), q.Location);
                default:
                    return node;
            }
        }

        return Visit(raw, true);
    }

    /// <summary>
    /// The value of a <c>for_each</c> items expression when it is known at load: a literal list reached
    /// through parameters (<c>params.angles</c>), or null when it is known only at binding.
    /// </summary>
    YNode? StaticValue(string text, Scope scope)
    {
        var match = ParamPath().Match(text.Trim());
        if (!match.Success)
            return null;
        var name = match.Groups[1].Value;
        var docScope = scope.DocumentScope;
        var decl = docScope.Document.Parameter(name);
        if (docScope.Kind == ScopeKind.Fragment && docScope.Bindings.TryGetValue(name, out var bound))
            return Literal(bound, docScope.Parent!);
        if (docScope.Kind == ScopeKind.Template && options.Parameters?.TryGetValue(name, out var given) == true)
            return given;
        return decl?.Default is { } dflt ? Literal(dflt, docScope) : null;
    }

    YNode? Literal(YNode node, Scope scope) =>
        node.IsSingleKeyMap("expr", out var e) ? e is YScalar s ? StaticValue(s.Text, scope) : null
        : node is YUnbound ? null
        : node;

    // ---- Expressions outside steps -------------------------------------------------------------

    void CollectDefaultGuards(Defaults defaults)
    {
        foreach (var route in defaults.ByClass.Values.Concat(defaults.ByOutcome.Values))
            plan.OtherExpressions.AddRange(StepReader.Guards(route.Target));
    }

    void CollectDocumentExpressions(DocumentInfo info, Scope scope)
    {
        foreach (var p in info.Parameters)
            if (p.Default is { } dflt)
                CollectExpressions(dflt, scope, $"parameter {p.Name} default", null, plan.OtherExpressions);
        if (info.BranchPattern is { } pattern)
            foreach (var e in Expressions.TemplateText.Expressions(pattern))
                plan.OtherExpressions.Add(new Expression(e.Text, ExpressionUse.Text, scope, info.BranchPatternLocation!, "metadata.branch_pattern"));
    }

    static void CollectExpressions(YNode node, Scope scope, string where, string? stepId, List<Expression> into)
    {
        switch (node)
        {
            case YMap m when m.IsSingleKeyMap("expr", out var e):
                if (e is YScalar s)
                    into.Add(new Expression(s.Text, ExpressionUse.Value, scope, s.Location, where, stepId));
                break;
            case YMap m:
                foreach (var (key, value) in m.Entries)
                    CollectExpressions(value, scope, $"{where}.{key.Text}", stepId, into);
                break;
            case YSeq q:
                foreach (var item in q.Items)
                    CollectExpressions(item, scope, where, stepId, into);
                break;
        }
    }

    // ---- Route resolution ----------------------------------------------------------------------

    void ResolveRoutes()
    {
        foreach (var step in plan.AllSteps)
        {
            var contract = step.Action is null ? null : Actions.All.GetValueOrDefault(step.Action);
            var unknown = (step.Form == StepForm.Action && contract is null)
                          || (step.Form is StepForm.Agent or StepForm.Decision && step.DeclaredOutcomes.Count == 0);
            if (unknown)
            {
                // Checks 2 and 6 report the step; its routes as written still count, so nothing else cascades.
                foreach (var route in step.Routes)
                    if (Target(route.Target, route.Scope, step) is { } t)
                        step.ResolvedRoutes.Add(new ResolvedRoute(route.Outcome, OutcomeClass.None, t, RouteSource.Step));
                continue;
            }
            foreach (var outcome in step.Outcomes(contract))
                if (Resolve(step, outcome) is { } resolved)
                    step.ResolvedRoutes.Add(resolved);
        }
    }

    ResolvedRoute? Resolve(Step step, Outcome outcome)
    {
        var own = step.Routes.FirstOrDefault(r => r.Outcome == outcome.Name);
        if (own is not null)
            return Target(own.Target, own.Scope, step) is { } t ? new ResolvedRoute(outcome.Name, outcome.Class, t, RouteSource.Step) : null;

        if (FromDefaults(step.Scope, outcome) is { } fromDefaults)
            return fromDefaults;

        if (outcome.Class == OutcomeClass.Success)
            return new ResolvedRoute(outcome.Name, outcome.Class, Next(step.Lane, step.LaneIndex + 1, step.Scope), RouteSource.Next);
        if (outcome.Name == Actions.AttemptsExhausted)
            return new ResolvedRoute(outcome.Name, outcome.Class, new ResolvedEscalate(null, null), RouteSource.EngineDefault);

        // In a fragment validated on its own, the caller's defaults are unknown: a classed outcome may be routed there.
        if (plan.Root.Standalone && outcome.Class != OutcomeClass.None)
            return new ResolvedRoute(outcome.Name, outcome.Class, new ResolvedExit(CallerDefault), RouteSource.ByClass);

        d.Add(Checks.OutcomeRouted,
            $"outcome '{outcome.Name}' (class {outcome.Class.Name()}) has no route after defaults are applied",
            step.RoutesLocation ?? step.OutcomesLocation ?? step.Location, step.Id);
        return null;
    }

    /// <summary>
    /// Route defaults from the step's document, then from each caller up to the template (LOG 71: a
    /// fragment's steps take the caller's defaults where the fragment sets none).
    /// </summary>
    ResolvedRoute? FromDefaults(Scope scope, Outcome outcome)
    {
        for (var s = scope.DocumentScope; s is not null; s = s.Parent?.DocumentScope)
        {
            var defaults = s.Document.Defaults;
            if (defaults.ByOutcome.TryGetValue(outcome.Name, out var byOutcome))
                return Target(byOutcome.Target, s, null) is { } t ? new ResolvedRoute(outcome.Name, outcome.Class, t, RouteSource.ByOutcome) : null;
            if (outcome.Class != OutcomeClass.None && defaults.ByClass.TryGetValue(outcome.Class, out var byClass))
                return Target(byClass.Target, s, null) is { } t ? new ResolvedRoute(outcome.Name, outcome.Class, t, RouteSource.ByClass) : null;
        }

        return null;
    }

    Resolved Next(string laneId, int index, Scope scope)
    {
        var lane = lanes[laneId];
        if (index < lane.Count)
            return new ResolvedStep(lane[index].Id, false);
        if (laneId.Length > 0)
            return new ResolvedBranchEnd(laneId[..laneId.LastIndexOf('#')]);
        return scope.DocumentScope.Standalone ? new ResolvedExit("(next)") : new ResolvedEnd("completed");
    }

    Resolved? Target(RouteTarget target, Scope scope, Step? from)
    {
        switch (target)
        {
            case ToStep to:
                if (FindStep(to.Id, scope) is { } id)
                    return new ResolvedStep(id, to.CleanUp);
                d.Add(Checks.RouteTarget, $"route target '{to.Id}' is not a step{(scope.Prefix.Length > 0 ? $" in {scope.Id}" : "")}", to.Location, from?.Id);
                return null;
            case EndRun end:
                return new ResolvedEnd(end.Status);
            case Escalate escalate:
                return new ResolvedEscalate(escalate.DecisionType, escalate.Prompt);
            case ExitFragment exit:
                return Exit(exit, scope, from);
            case Guarded guarded:
                var entries = new List<(Expression?, Resolved)>();
                foreach (var entry in guarded.Entries)
                    if (Target(entry.Target, scope, from) is { } t)
                        entries.Add((entry.When, t));
                return entries.Count == guarded.Entries.Count ? new ResolvedGuarded(entries) : null;
            default:
                return null;
        }
    }

    /// <summary>A step id or fragment use id written in <paramref name="scope"/>, as a full step id.</summary>
    string? FindStep(string id, Scope scope)
    {
        var full = scope.Prefix + id;
        if (stepsById.ContainsKey(full))
            return full;
        if (instanceSpans.TryGetValue(full, out var span) && span.Last >= span.First)
            return lanes[span.Lane][span.First].Id;
        return null;
    }

    /// <summary>
    /// An exit routes like an outcome of the <c>use</c> entry: the entry's route, the caller's defaults, or
    /// for class success the step after the fragment (docs/03 section 11).
    /// </summary>
    Resolved? Exit(ExitFragment exit, Scope scope, Step? from)
    {
        var fragment = scope.DocumentScope;
        if (fragment.Kind != ScopeKind.Fragment)
        {
            d.Add(Checks.RouteTarget, $"exit '{exit.Exit}' is used outside a fragment", exit.Location, from?.Id);
            return null;
        }

        var declared = fragment.Document.Exits.FirstOrDefault(e => e.Name == exit.Exit);
        if (declared is null)
        {
            d.Add(Checks.RouteTarget, $"exit '{exit.Exit}' is not one of the fragment's exits ({string.Join(", ", fragment.Document.Exits.Select(e => e.Name))})", exit.Location, from?.Id);
            return null;
        }

        if (fragment.Standalone)
            return new ResolvedExit(exit.Exit);
        if (fragment.BranchRoot)
            return new ResolvedBranchEnd(fragment.Parent!.ContainerId!);

        var route = fragment.ExitRoutes.FirstOrDefault(r => r.Outcome == exit.Exit);
        if (route is not null)
            return Target(route.Target, fragment.Parent!, null);
        var caller = fragment.Parent!;
        if (FromDefaults(caller, declared) is { } fromDefaults)
            return fromDefaults.Target;
        if (declared.Class == OutcomeClass.Success)
        {
            var span = instanceSpans[fragment.UseId!];
            return Next(span.Lane, span.Last + 1, caller);
        }

        d.Add(Checks.Fragment, $"exit '{exit.Exit}' (class {declared.Class.Name()}) of fragment use '{fragment.UseId}' is not routed", fragment.UseLocation, fragment.UseId);
        return null;
    }
}
