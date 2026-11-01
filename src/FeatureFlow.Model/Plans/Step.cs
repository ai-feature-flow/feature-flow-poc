using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Plans;

public enum StepForm
{
    Agent,
    Decision,
    Action,
    Parallel,
    Chain,
}

public enum ExpressionUse
{
    Guard,
    Value,
    Items,
    Text,
}

/// <summary>
/// A CEL expression found in a plan, with the scope it is evaluated in. <see cref="ExtraVariables"/> are the
/// <c>with</c> keys a task or prompt is rendered with (docs/03 section 6.3).
/// </summary>
public sealed record Expression(
    string Text,
    ExpressionUse Use,
    Scope Scope,
    SourceLocation Location,
    string Where,
    string? StepId = null,
    IReadOnlyList<string>? ExtraVariables = null);

public abstract record RouteTarget(SourceLocation Location);

public sealed record ToStep(string Id, bool CleanUp, SourceLocation Location) : RouteTarget(Location);

public sealed record EndRun(string Status, SourceLocation Location) : RouteTarget(Location);

public sealed record Escalate(string? DecisionType, ItemRef? Prompt, SourceLocation Location) : RouteTarget(Location);

public sealed record ExitFragment(string Exit, SourceLocation Location) : RouteTarget(Location);

public sealed record GuardEntry(Expression? When, RouteTarget Target);

public sealed record Guarded(IReadOnlyList<GuardEntry> Entries, SourceLocation Location) : RouteTarget(Location);

/// <summary>A route as written: an outcome and its target, in the scope it was written in.</summary>
public sealed record RouteSpec(string Outcome, RouteTarget Target, Scope Scope, SourceLocation Location);

public abstract record Resolved;

public sealed record ResolvedStep(string StepId, bool CleanUp) : Resolved;

public sealed record ResolvedEnd(string Status) : Resolved;

public sealed record ResolvedEscalate(string? DecisionType, ItemRef? Prompt) : Resolved;

/// <summary>
/// The end of a branch: the outcome's class is reported to the join (docs/03 section 8.1). <see cref="Class"/>
/// is set when the branch ends by a fragment's exit, whose class is the one reported; otherwise the outcome's
/// own class is.
/// </summary>
public sealed record ResolvedBranchEnd(string ContainerId, OutcomeClass? Class = null) : Resolved;

/// <summary>Leaving a fragment validated on its own, where no caller routes the exit.</summary>
public sealed record ResolvedExit(string Exit) : Resolved;

public sealed record ResolvedGuarded(IReadOnlyList<(Expression? When, Resolved Target)> Entries) : Resolved;

public enum RouteSource
{
    Step,
    ByOutcome,
    ByClass,
    Next,
    EngineDefault,
}

/// <summary>The route an outcome takes after defaults are applied (docs/03 section 4.3).</summary>
public sealed record ResolvedRoute(string Outcome, OutcomeClass Class, Resolved Target, RouteSource Source);

public sealed record AgentSpec(
    ItemRef? Role,
    ItemRef? Task,
    IReadOnlyList<ItemRef> Skills,
    string? WriteAccess,
    string Context,
    PlanningSpec? Planning,
    bool Push,
    string OnUncommitted,
    SourceLocation? ToolsLocation);

public sealed record PlanningSpec(IReadOnlyList<string> Targets, IReadOnlyList<ItemRef> Templates, string Review);

public sealed record SessionFile(string Path, string Name, ItemRef? Type);

public sealed record SessionSpec(YNode? Repository, YNode? Folder, IReadOnlyList<SessionFile> Files, IReadOnlyList<string> ChecksFrom, SourceLocation Location);

public sealed record DecisionSpec(string? Type, SourceLocation TypeLocation, ItemRef? Prompt, string? Auto, SessionSpec? Session);

/// <summary>An artifact a step produces; <see cref="Outcome"/> null means every success outcome.</summary>
public sealed record ArtifactOut(string? Outcome, string Name, ItemRef? Type, SourceLocation Location);

/// <summary>An artifact a step consumes. <see cref="From"/> is current, parent, root, or child:&lt;id&gt;.</summary>
public sealed record ArtifactIn(string Name, string From, bool Optional, ItemRef? Type, SourceLocation Location);

public sealed record OutputDecl(string Name, string Type, SourceLocation Location);

public sealed record Verdict(IReadOnlyList<string> Accept, IReadOnlyList<string> Reject);

public sealed record Limits(long? Attempts, string? Timeout, long? CorrectionTurns, long? QuestionsMax, string? QuestionsTimeout, long? Races);

public sealed record TrackerSpec(string? OnStart, IReadOnlyDictionary<string, string> On, SourceLocation Location);

public sealed class Branch
{
    public required int Index { get; init; }
    public required Scope Scope { get; init; }
    public List<Step> Steps { get; } = [];
}

public sealed class Container
{
    public required string Mode { get; init; }
    public string Join { get; init; } = "all";
    public long? MaxConcurrency { get; init; }
    public bool FromForEach { get; init; }
    public Expression? Items { get; init; }
    public bool ItemsKnown { get; init; } = true;
    public List<Branch> Branches { get; } = [];
}

/// <summary>One step of an expanded plan. Ids are final: fragment and branch prefixes applied.</summary>
public sealed class Step
{
    public required string Id { get; init; }
    public required StepForm Form { get; init; }
    public required Scope Scope { get; init; }
    public required SourceLocation Location { get; init; }

    /// <summary>The step's id as written in its document, before prefixes.</summary>
    public required string LocalId { get; init; }

    public string? Name { get; set; }
    public AgentSpec? Agent { get; set; }
    public DecisionSpec? Decision { get; set; }
    public string? Action { get; set; }
    public YMap? With { get; set; }

    /// <summary>Outcomes declared by an agent step or decision; empty for actions and containers.</summary>
    public List<Outcome> DeclaredOutcomes { get; } = [];

    public SourceLocation? OutcomesLocation { get; set; }
    public List<RouteSpec> Routes { get; } = [];
    public SourceLocation? RoutesLocation { get; set; }
    public List<ArtifactOut> Produces { get; } = [];
    public List<ArtifactIn> Consumes { get; } = [];
    public Dictionary<string, Verdict> Verdicts { get; } = [];
    public Dictionary<string, IReadOnlyList<string>> Ratifies { get; } = [];
    public List<OutputDecl> Outputs { get; } = [];
    public Limits? Limits { get; set; }
    public YNode? Repository { get; set; }
    public bool Protected { get; set; }
    public TrackerSpec? Tracker { get; set; }
    public List<Expression> Expressions { get; } = [];

    /// <summary>Names bound with <c>{ from_artifact: name }</c> in <c>with</c> (docs/03 section 6.3).</summary>
    public List<(string Name, SourceLocation Location)> FromArtifacts { get; } = [];

    public Container? Container { get; set; }

    /// <summary>The sequence the step sits in: "" for the plan's top level, or a container id and branch index.</summary>
    public string Lane { get; set; } = "";

    public int LaneIndex { get; set; }

    /// <summary>Whether this step was a branch's single step or the last of a branch.</summary>
    public bool InBranch => Scope.Kind == ScopeKind.Branch || Lane.Length > 0;

    public List<ResolvedRoute> ResolvedRoutes { get; } = [];

    /// <summary>An agent step's write access after its role is applied: the step's value, which may only tighten the role's.</summary>
    public string? EffectiveWriteAccess { get; set; }

    /// <summary>The outcomes the step can end with: declared, fixed by its action, or a join's.</summary>
    public IReadOnlyList<Outcome> Outcomes(ActionContract? contract = null)
    {
        var list = new List<Outcome>();
        switch (Form)
        {
            case StepForm.Agent or StepForm.Decision:
                list.AddRange(DeclaredOutcomes);
                break;
            case StepForm.Action:
                if ((contract ?? (Action is null ? null : Actions.All.GetValueOrDefault(Action))) is { } c)
                    list.AddRange(c.Outcomes);
                break;
            default:
                list.AddRange(Actions.JoinOutcomes);
                break;
        }

        if (ConsumesAttempts)
            list.Add(new Outcome(Actions.AttemptsExhausted, OutcomeClass.Failure));
        return list;
    }

    /// <summary>Agent steps and actions use attempts; decisions and joins do not (docs/01 section 3.2).</summary>
    public bool ConsumesAttempts => Form is StepForm.Agent or StepForm.Action;

    /// <summary>The step's map as written, after fragment substitution, so a patch can change one field (docs/03 section 10.2).</summary>
    public YMap? Source { get; set; }

    public override string ToString() => Id;
}

/// <summary>A <c>use</c> of a fragment, kept for the readable form and for declarative tracker updates.</summary>
public sealed record FragmentInstance(string Id, ItemRef Fragment, Scope Scope, bool Protected, TrackerSpec? Tracker, SourceLocation Location)
{
    public string? FirstStepId { get; set; }
}

/// <summary>A template after fragment and <c>for_each</c> expansion, with routes resolved.</summary>
public sealed class Plan
{
    public required DocumentInfo Document { get; init; }
    public required Scope Root { get; init; }

    /// <summary>The top-level sequence. Containers hold their branches' steps.</summary>
    public List<Step> Steps { get; } = [];

    public List<FragmentInstance> Instances { get; } = [];

    /// <summary>Fragment parameters used through <c>{ param: }</c>, as "&lt;scope id&gt;|&lt;name&gt;".</summary>
    public HashSet<string> SubstitutedParameters { get; } = [];

    /// <summary>Expressions outside steps: fragment bindings, parameter defaults, the branch pattern.</summary>
    public List<Expression> OtherExpressions { get; } = [];

    /// <summary>Every step, depth first, in document order: a container before its branches' steps.</summary>
    public IEnumerable<Step> AllSteps => Walk(Steps);

    static IEnumerable<Step> Walk(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Container is { } container)
                foreach (var branch in container.Branches)
                    foreach (var inner in Walk(branch.Steps))
                        yield return inner;
        }
    }

    public IEnumerable<Expression> AllExpressions => AllSteps.SelectMany(s => s.Expressions).Concat(OtherExpressions);
}
