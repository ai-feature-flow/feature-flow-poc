using System.Text.RegularExpressions;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Plans;

/// <summary>
/// Binds one step's YAML map to a <see cref="Step"/>: the fields every form shares and the settings of
/// agent steps, decisions and actions (docs/03 section 2). Containers' branches and fragment uses are
/// expanded by the library loader, which calls this for each step it produces.
/// </summary>
public static partial class StepReader
{
    public static readonly IReadOnlyList<string> FormKeys = ["agent", "decision", "action", "parallel", "for_each", "use"];

    public static readonly IReadOnlyList<string> EndStatuses = ["completed", "failed", "rejected", "partial"];

    public static readonly IReadOnlyList<string> OutputTypes = ["string", "number", "boolean", "array"];

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    public static partial Regex SnakeCase();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex OutcomeName();

    [GeneratedRegex(@"^[0-9]+[smhd]$")]
    private static partial Regex Duration();

    /// <summary>Reads the fields shared by every form. The caller has read <c>id</c> and the form key.</summary>
    public static Step Read(MapReader r, string fullId, string localId, StepForm form, Scope scope, DiagnosticBag d)
    {
        var step = new Step { Id = fullId, LocalId = localId, Form = form, Scope = scope, Location = r.Map.Location };
        step.Name = r.String("name");

        switch (form)
        {
            case StepForm.Agent:
                step.Agent = ReadAgent(r.Node("agent"), step, d);
                break;
            case StepForm.Decision:
                step.Decision = ReadDecision(r.Node("decision"), step, d);
                break;
            case StepForm.Action:
                step.Action = r.String("action", required: true);
                break;
        }

        ReadWith(r, step, d);
        ReadOutcomes(r, step, d);
        ReadRoutes(r, step, d);
        ReadProduces(r, step, d);
        ReadConsumes(r, step, d);
        ReadVerdicts(r, step, d);
        ReadRatifies(r, step, d);
        ReadOutputs(r, step, d);
        step.Limits = ReadLimits(r.Node("limits"), d, step.Id);
        step.Repository = r.Node("repository");
        if (step.Repository is not null)
            CollectExpressions(step.Repository, step, "repository");
        step.Protected = r.Bool("protected") ?? false;
        step.Tracker = ReadTracker(r.Node("tracker"), d, step.Id);
        return step;
    }

    static AgentSpec? ReadAgent(YNode? node, Step step, DiagnosticBag d)
    {
        var a = MapReader.Of(node, d, "agent", step.Id);
        if (a is null)
            return null;
        var role = LibraryItemReader.Ref(a.Required("role"), a, "agent.role");
        var task = LibraryItemReader.Ref(a.Required("task"), a, "agent.task");
        var skills = new List<ItemRef>();
        foreach (var item in LibraryItemReader.Seq(a.Node("skills"), d, "agent.skills"))
            if (LibraryItemReader.Ref(item, a, "agent.skills entry") is { } s)
                skills.Add(s);
        var access = a.Enum("write_access", WriteAccess.Levels);
        var context = a.Enum("context", ["full", "none"]) ?? "full";
        PlanningSpec? planning = null;
        if (MapReader.Of(a.Node("planning"), d, "agent.planning", step.Id) is { } p)
        {
            var targets = p.StringList("targets");
            foreach (var t in targets.Where(t => t is not ("self" or "parent")))
                p.Error($"planning target must be self or parent, not '{t}'", p.Map.Location);
            var templates = new List<ItemRef>();
            foreach (var item in LibraryItemReader.Seq(p.Node("templates"), d, "planning.templates"))
                if (LibraryItemReader.Ref(item, p, "planning.templates entry") is { } t)
                    templates.Add(t);
            var review = p.Enum("review", ["auto", "required"]) ?? "auto";
            p.Finish();
            planning = new PlanningSpec(targets, templates, review);
        }

        var push = a.Bool("push") ?? true;
        var onUncommitted = a.Enum("on_uncommitted", ["commit", "fail"]) ?? "commit";

        // A step may not set tools (F10). Reported as check 13, not as an unknown key.
        var tools = a.Node("tools");
        a.Node("toolsets");
        var toolsLocation = tools?.Location ?? (a.Map["toolsets"]?.Location);
        a.Finish();
        return new AgentSpec(role, task, skills, access, context, planning, push, onUncommitted, toolsLocation);
    }

    static DecisionSpec? ReadDecision(YNode? node, Step step, DiagnosticBag d)
    {
        var m = MapReader.Of(node, d, "decision", step.Id);
        if (m is null)
            return null;
        var typeNode = m.Required("type");
        var type = m.AsString(typeNode, "decision.type");
        var prompt = LibraryItemReader.Ref(m.Node("prompt"), m, "decision.prompt");
        var auto = m.String("auto");
        SessionSpec? session = null;
        if (MapReader.Of(m.Node("session"), d, "decision.session", step.Id) is { } s)
        {
            var repository = s.Node("repository");
            var folder = s.Node("folder");
            if (repository is not null)
                CollectExpressions(repository, step, "session.repository");
            if (folder is not null)
                CollectExpressions(folder, step, "session.folder");
            var files = new List<SessionFile>();
            foreach (var item in LibraryItemReader.Seq(s.Required("files"), d, "session.files"))
                if (MapReader.Of(item, d, "a session file", step.Id) is { } f)
                {
                    var path = f.String("path", required: true) ?? "";
                    var name = f.String("name", required: true) ?? "";
                    var fileType = LibraryItemReader.Ref(f.Required("type"), f, "session file type");
                    f.Finish();
                    files.Add(new SessionFile(path, name, fileType));
                }

            var checksFrom = s.StringList("checks_from");
            s.Finish();
            session = new SessionSpec(repository, folder, files, checksFrom, s.Map.Location);
        }

        m.Finish();
        return new DecisionSpec(type, typeNode?.Location ?? m.Map.Location, prompt, auto, session);
    }

    static void ReadWith(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("with");
        switch (node)
        {
            case null or YScalar { IsNull: true }:
                return;
            case YMap map:
                step.With = map;
                CollectExpressions(map, step, "with");
                break;
            default:
                r.Error($"with must be a map, not {node.Describe()}", node.Location);
                break;
        }
    }

    /// <summary>Finds <c>{ expr: }</c> and <c>{ from_artifact: }</c> values anywhere inside a value.</summary>
    public static void CollectExpressions(YNode node, Step step, string where)
    {
        switch (node)
        {
            case YMap m when m.IsSingleKeyMap("expr", out var e):
                if (e is YScalar s)
                    step.Expressions.Add(new Expression(s.Text, ExpressionUse.Value, step.Scope, s.Location, where, step.Id));
                break;
            case YMap m when m.IsSingleKeyMap("from_artifact", out var a):
                if (a is YScalar s2)
                    step.FromArtifacts.Add((s2.Text, s2.Location));
                break;
            case YMap m:
                foreach (var (key, value) in m.Entries)
                    CollectExpressions(value, step, $"{where}.{key.Text}");
                break;
            case YSeq q:
                for (var i = 0; i < q.Items.Count; i++)
                    CollectExpressions(q.Items[i], step, $"{where}[{i}]");
                break;
        }
    }

    static void ReadOutcomes(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("outcomes");
        if (node is null)
            return;
        if (step.Form is not (StepForm.Agent or StepForm.Decision))
        {
            r.Error("only agent steps and decisions declare outcomes; actions and joins have fixed outcomes", node.Location);
            return;
        }

        step.OutcomesLocation = node.Location;
        if (MapReader.Of(node, d, "outcomes", step.Id) is not { } m)
            return;
        foreach (var (key, value) in m.Map.Entries)
        {
            m.Node(key.Text);
            if (!OutcomeName().IsMatch(key.Text))
                m.Error($"outcome name '{key.Text}' must start with a letter and use letters, digits and _", key.Location);
            var cls = value is YScalar { IsNull: true } ? OutcomeClass.None : OutcomeClasses.Parse(value.ScalarText());
            if (cls is null)
            {
                m.Error($"outcome '{key.Text}' has class {value.Describe()}; classes are success, failure, blocked or ~", value.Location);
                continue;
            }

            step.DeclaredOutcomes.Add(new Outcome(key.Text, cls.Value));
        }
    }

    static void ReadRoutes(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("routes");
        if (node is null)
            return;
        step.RoutesLocation = node.Location;
        if (MapReader.Of(node, d, "routes", step.Id) is not { } m)
            return;
        foreach (var (key, value) in m.Map.Entries)
        {
            m.Node(key.Text);
            if (ReadTarget(value, step.Scope, d, step.Id, $"route {key.Text}") is { } target)
            {
                step.Routes.Add(new RouteSpec(key.Text, target, step.Scope, key.Location));
                step.Expressions.AddRange(Guards(target));
            }
        }
    }

    public static IEnumerable<Expression> Guards(RouteTarget target) => target switch
    {
        Guarded g => g.Entries.SelectMany(e => (e.When is null ? [] : new[] { e.When }).Concat(Guards(e.Target))),
        _ => [],
    };

    /// <summary>
    /// Reads a route target: a step id, <c>{ to }</c>, <c>{ end }</c>, <c>{ escalate }</c>, <c>{ exit }</c>,
    /// or a guarded list (docs/03 section 4.1).
    /// </summary>
    public static RouteTarget? ReadTarget(YNode node, Scope scope, DiagnosticBag d, string? stepId, string where)
    {
        switch (node)
        {
            case YScalar { IsNull: false } s:
                return new ToStep(s.Text, false, s.Location);
            case YSeq seq:
                var entries = new List<GuardEntry>();
                foreach (var item in seq.Items)
                {
                    if (MapReader.Of(item, d, $"{where} entry", stepId) is not { } e)
                        continue;
                    Expression? when = null;
                    if (e.Node("when") is { } w)
                    {
                        if (w is YScalar { IsNull: false } ws)
                            when = new Expression(ws.Text, ExpressionUse.Guard, scope, ws.Location, where, stepId);
                        else
                            e.Error("when must be a CEL expression string", w.Location);
                    }

                    if (ReadDirective(e, scope, d, stepId, where) is { } t)
                        entries.Add(new GuardEntry(when, t));
                }

                return new Guarded(entries, seq.Location);
            case YMap map:
                var r = new MapReader(map, d, where, stepId);
                return ReadDirective(r, scope, d, stepId, where);
            default:
                d.Add(Checks.Schema, $"{where} has no target", node.Location, stepId);
                return null;
        }
    }

    static RouteTarget? ReadDirective(MapReader r, Scope scope, DiagnosticBag d, string? stepId, string where)
    {
        var keys = new[] { "to", "end", "escalate", "exit" }.Where(r.Has).ToList();
        if (keys.Count != 1)
        {
            r.Error($"{where} must have exactly one of to, end, escalate or exit", r.Map.Location);
            foreach (var k in r.Map.Keys)
                r.Node(k);
            return null;
        }

        RouteTarget? target = null;
        switch (keys[0])
        {
            case "to":
                var to = r.Node("to")!;
                var cleanUp = r.Bool("clean_up") ?? false;
                if (to is YMap)
                {
                    target = ReadTarget(to, scope, d, stepId, where);
                    if (cleanUp && target is ToStep ts)
                        target = ts with { CleanUp = true };
                }
                else if (to is YScalar { IsNull: false } s)
                    target = new ToStep(s.Text, cleanUp, s.Location);
                else
                    r.Error($"{where}: to must be a step id", to.Location);
                break;
            case "end":
                var status = r.String("end");
                if (status is null || !EndStatuses.Contains(status))
                    r.Error($"{where}: end must be one of {string.Join(", ", EndStatuses)}", r.Map["end"]!.Location);
                else
                    target = new EndRun(status, r.Map["end"]!.Location);
                break;
            case "escalate":
                if (r.Bool("escalate") != true)
                    r.Error($"{where}: escalate must be true", r.Map["escalate"]!.Location);
                var type = r.String("type");
                var prompt = LibraryItemReader.Ref(r.Node("prompt"), r, "escalate prompt");
                target = new Escalate(type, prompt, r.Map.Location);
                break;
            case "exit":
                var exit = r.String("exit");
                if (exit is not null)
                    target = new ExitFragment(exit, r.Map["exit"]!.Location);
                break;
        }

        r.Finish();
        return target;
    }

    static void ReadProduces(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("produces");
        switch (node)
        {
            case null or YScalar { IsNull: true }:
                return;
            case YSeq seq:
                ReadArtifactList(seq, null, step, d);
                break;
            case YMap map:
                foreach (var (key, value) in map.Entries)
                {
                    if (value is YSeq list)
                        ReadArtifactList(list, key.Text, step, d);
                    else
                        d.Add(Checks.Schema, $"produces.{key.Text} must be a list", value.Location, step.Id);
                }

                break;
            default:
                r.Error("produces must be a list or a map from outcome to list", node.Location);
                break;
        }
    }

    static void ReadArtifactList(YSeq seq, string? outcome, Step step, DiagnosticBag d)
    {
        foreach (var item in seq.Items)
        {
            if (MapReader.Of(item, d, "a produced artifact", step.Id) is not { } a)
                continue;
            var name = a.String("name", required: true);
            var type = LibraryItemReader.Ref(a.Required("type"), a, "produced artifact type");
            a.Finish();
            if (name is not null)
                step.Produces.Add(new ArtifactOut(outcome, name, type, item.Location));
        }
    }

    static void ReadConsumes(MapReader r, Step step, DiagnosticBag d)
    {
        foreach (var item in LibraryItemReader.Seq(r.Node("consumes"), d, "consumes"))
        {
            if (MapReader.Of(item, d, "a consumed artifact", step.Id) is not { } c)
                continue;
            var name = c.String("name", required: true);
            var fromNode = c.Node("from");
            var from = "current";
            if (fromNode is YScalar { IsNull: false } fs)
            {
                if (fs.Text is "current" or "parent" or "root")
                    from = fs.Text;
                else
                    c.Error($"from must be current, parent, root or {{ child: <step id> }}, not '{fs.Text}'", fs.Location);
            }
            else if (fromNode is not null && fromNode.IsSingleKeyMap("child", out var child) && child.ScalarText() is { } childId)
                from = $"child:{childId}";
            else if (fromNode is not null)
                c.Error("from must be current, parent, root or { child: <step id> }", fromNode.Location);

            var optional = c.Bool("optional") ?? false;
            var type = LibraryItemReader.Ref(c.Node("type"), c, "consumed artifact type");
            c.Finish();
            if (name is not null)
                step.Consumes.Add(new ArtifactIn(name, from, optional, type, item.Location));
        }
    }

    static void ReadVerdicts(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("verdicts");
        if (node is null)
            return;
        if (step.Form is not (StepForm.Agent or StepForm.Decision))
            r.Error("only agent steps and decisions give verdicts", node.Location);
        if (MapReader.Of(node, d, "verdicts", step.Id) is not { } m)
            return;
        foreach (var (key, value) in m.Map.Entries)
        {
            m.Node(key.Text);
            if (MapReader.Of(value, d, $"verdicts.{key.Text}", step.Id) is not { } v)
                continue;
            var verdict = new Verdict(v.StringList("accept"), v.StringList("reject"));
            v.Finish();
            step.Verdicts[key.Text] = verdict;
        }
    }

    static void ReadRatifies(MapReader r, Step step, DiagnosticBag d)
    {
        var node = r.Node("ratifies");
        if (node is null)
            return;
        if (step.Form is not StepForm.Decision)
            r.Error("only decisions ratify artifacts (docs/03 section 7.4)", node.Location);
        if (MapReader.Of(node, d, "ratifies", step.Id) is not { } m)
            return;
        foreach (var (key, _) in m.Map.Entries)
            step.Ratifies[key.Text] = m.StringList(key.Text);
    }

    static void ReadOutputs(MapReader r, Step step, DiagnosticBag d)
    {
        if (MapReader.Of(r.Node("outputs"), d, "outputs", step.Id) is not { } m)
            return;
        if (m.Map.Entries.Count > 16)
            m.Error("a step declares at most 16 outputs (docs/03 section 5.4)", m.Map.Location);
        foreach (var (key, value) in m.Map.Entries)
        {
            m.Node(key.Text);
            if (MapReader.Of(value, d, $"outputs.{key.Text}", step.Id) is not { } o)
                continue;
            var type = o.Enum("type", OutputTypes, required: true);
            o.String("description");
            o.Finish();
            if (type is not null)
                step.Outputs.Add(new OutputDecl(key.Text, type, key.Location));
        }
    }

    public static Limits? ReadLimits(YNode? node, DiagnosticBag d, string? stepId)
    {
        if (MapReader.Of(node, d, "limits", stepId) is not { } l)
            return null;
        var attempts = l.Int("attempts");
        var timeout = DurationValue(l, "timeout");
        var turns = l.Int("correction_turns");
        long? qMax = null;
        string? qTimeout = null;
        if (MapReader.Of(l.Node("questions"), d, "limits.questions", stepId) is { } q)
        {
            qMax = q.Int("max");
            qTimeout = DurationValue(q, "timeout");
            q.Finish();
        }

        var races = l.Int("races");
        l.Finish();
        foreach (var (name, value) in new[] { ("attempts", attempts), ("correction_turns", turns), ("races", races), ("questions.max", qMax) })
            if (value is < 0)
                l.Error($"limits.{name} must not be negative", l.Map.Location);
        return new Limits(attempts, timeout, turns, qMax, qTimeout, races);
    }

    static string? DurationValue(MapReader r, string key)
    {
        var value = r.String(key);
        if (value is not null && !Duration().IsMatch(value))
            r.Error($"{key} must be a duration such as 30m or 4h, not '{value}'", r.Map[key]!.Location);
        return value;
    }

    public static TrackerSpec? ReadTracker(YNode? node, DiagnosticBag d, string? stepId)
    {
        if (MapReader.Of(node, d, "tracker", stepId) is not { } t)
            return null;
        var onStart = t.String("on_start");
        var on = new Dictionary<string, string>();
        if (MapReader.Of(t.Node("on"), d, "tracker.on", stepId) is { } m)
            foreach (var (key, _) in m.Map.Entries)
                if (m.String(key.Text) is { } status)
                    on[key.Text] = status;
        t.Finish();
        return new TrackerSpec(onStart, on, t.Map.Location);
    }
}
