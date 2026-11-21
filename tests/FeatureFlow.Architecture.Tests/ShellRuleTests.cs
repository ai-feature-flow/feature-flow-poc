using System.Reflection;
using FeatureFlow.Engine;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FeatureFlow.Architecture.Tests;

/// <summary>
/// The Temporal shell is a loop (docs/02 section 3): it carries out the core's commands and gives back what happened, and
/// holds no routing decision the core can make (review F8). Two rules, each of which fails on a shell that decides for itself:
/// no routing vocabulary in the shell's source, with the core read only through a listed set of its members and its state
/// never touched; and no routing state in the shell's fields. The first is a check of names, not a proof: a decision written
/// in names the rule does not know passes it (LOG 382).
/// </summary>
public class ShellRuleTests
{
    /// <summary>Types whose use means the shell is reading the plan or the core's state to decide something itself.</summary>
    static readonly IReadOnlySet<string> RoutingTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "WaitKind", "RecoveryKind", "StepForm", "OutcomeClass", "ActionDecisions", "Recoveries",
    };

    /// <summary>Members of a step or of the core's state; the shell reads them only through <see cref="RunCore"/> methods.</summary>
    static readonly IReadOnlySet<string> RoutingMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Form", "Action", "Decision", "Consumes", "Produces", "Waits", "Steps", "DeclaredOutcomes",
    };

    /// <summary>
    /// Every member of the core the shell may use, and why it is not a routing decision made by the shell. Anything else,
    /// <see cref="RunCore.State"/> above all, fails the rule: the shell neither reads the core's state nor writes it, and a
    /// new use fails until its reason is written here.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> CoreMembers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Apply"] = "gives the core an input: what happened",
        ["Advance"] = "asks for the core's commands",
        ["Flush"] = "asks for the core's commands while draining",
        ["Route"] = "the core's routing of an answer, which the answer update's validator and handler carry out",
        ["Answer"] = "applies an answer the core routed",
        ["RouteOverride"] = "the core's routing of an override, which the override update's validator and handler carry out",
        ["Override"] = "applies an override the core routed",
        ["Note"] = "notes on the timeline what an activity, signal or hand-over did",
        ["Status"] = "whether the run is still running, for the loop, the queries and the records",
        ["Children"] = "the running children's workflow ids, to signal, watch and count them",
        ["InFlight"] = "the steps running, for the query and a library override's record",
        ["Timeline"] = "the timeline not yet recorded, read for tracker updates",
        ["TakeTimeline"] = "drains the timeline into records",
        ["TakeProduced"] = "drains produced artifacts into the artifact index",
        ["TakeWaitChanges"] = "drains opened and closed waits into records",
        ["WaitViews"] = "the open waits, for the query and the hand-over record",
        ["Timers"] = "the timers to arm again after a hand-over",
        ["Snapshot"] = "the core's state carried over a hand-over",
        ["StepSpec"] = "what a step's activity is given, mapped to its call",
        ["StepKey"] = "a step execution's identity, passed to its activity",
        ["ScopeFacts"] = "what an engine action knows of the run, mapped to its scope",
        ["ExpandList"] = "the list a plan.expand step expands, read by its activity",
        ["IsCurrent"] = "whether an activity's result is for the execution still running",
        ["Executions"] = "a step's execution count, for an activity's identity",
        ["StepsRun"] = "the steps that ran, for a clean-up activity",
        ["WorkBranch"] = "the run's work branch, passed to activities",
        ["WaitsBetweenReads"] = "whether a step's wait activity is cancelled by a wake or a hand-over",
        ["ChildSpec"] = "what a child workflow starts with",
        ["ChildStep"] = "the step that started a child workflow, from its id",
        ["ChildrenStarted"] = "the child runs started, for the pull-requests query and the close activity",
        ["PullRequestsOpened"] = "the pull requests opened, for the query and the close activity",
        ["SessionStep"] = "the session a gate's answer closes, for the close activity",
        ["SessionFacts"] = "what a session opens with",
        ["SessionWorkspace"] = "a session's workspace, for its activity",
        ["SessionImport"] = "what a submission's import activity imports",
        ["CheckTarget"] = "the later step a pre-submit check runs",
        ["NoteCheckInputs"] = "records a check's inputs digest",
        ["IsSuccess"] = "whether a pre-submit check's outcome passed",
        ["LibraryFile"] = "where a library item is stored, for the override activity",
        ["LibraryItemError"] = "whether a parent's library override applies to this run",
        ["ProtectedResolving"] = "the protected steps a library override reaches, for its lock entry",
        ["Plan"] = "the plan, given whole to the interpreter's functions (tracker updates, work items); never read here",
        ["Start"] = "starts a core",
        ["Resume"] = "resumes a core from a carried snapshot",
    };

    /// <summary>Core members the shell may only pass on as an argument, never read a member of.</summary>
    static readonly IReadOnlySet<string> ArgumentOnly = new HashSet<string>(StringComparer.Ordinal) { "Plan" };

    /// <summary>The functions the shell may give the core to whole: the interpreter's own, which read it as the core does.</summary>
    static readonly IReadOnlySet<string> CoreTakers = new HashSet<string>(StringComparer.Ordinal) { "PlanExpansion.Build" };

    /// <summary>String methods that, called on a wait's id, decide something from how the id is spelled.</summary>
    static readonly IReadOnlySet<string> SpellingReads = new HashSet<string>(StringComparer.Ordinal)
    {
        "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Split", "Substring", "Replace", "Equals", "CompareTo",
    };

    /// <summary>
    /// Every instance field of the shell, and why it is Temporal's rather than the core's (docs/02 section 3). A new field fails
    /// this test until its reason is written here, which is the place to ask whether the core could hold it.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> TemporalFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["input"] = "the workflow's input, fixed for this run",
        ["core"] = "the interpreter's core",
        ["policy"] = "the run's policy, an activity's result fixed at run start and carried in the input",
        ["pendingChildEnds"] = "child-ended signals, which arrive before the core exists (DR14) and are confirmed by an activity",
        ["pendingParentPatches"] = "parent-patch signals, which arrive before the core exists (DR14)",
        ["cancelledByParent"] = "the parent-cancelled signal, which arrives before the core exists (DR14)",
        ["handOvers"] = "the hand-over records, SP1's evidence of each continue-as-new",
        ["timelineSequence"] = "record numbering for the record store",
        ["lastRecordedStatus"] = "record numbering: the status last written",
        ["trackedThrough"] = "record numbering: timeline entries examined for tracker updates",
        ["trackerUpdates"] = "tracker updates found but not yet made by their activity",
        ["lockRecords"] = "lock records not yet written by the record activity",
        ["draining"] = "drain: the run is handing over",
        ["dirty"] = "the loop's wake condition",
        ["inFlightActivities"] = "drain: activities the hand-over waits for",
        ["interruptedThisRun"] = "drain: waits this run interrupted, for its hand-over record",
        ["activitiesStartedWhileDraining"] = "drain: counted for this run's hand-over record (LOG 365)",
        ["childEndsWhileDraining"] = "drain: counted for this run's hand-over record (LOG 365)",
        ["answersWhileDraining"] = "drain: counted for this run's hand-over record (LOG 365)",
        ["stepTokens"] = "cancellation tokens of step activities",
        ["interrupted"] = "steps whose activity this run cancelled, until the cancellation lands",
        ["woken"] = "waits a wake cancelled, until the cancellation lands",
        ["childWatch"] = "the cancellation token of the carried-children watch activity",
        ["confirmsStarted"] = "child-end confirm activities in flight",
        ["cleanUpFailures"] = "clean-up activity results waiting for their step's start in the same batch",
        ["sessionChecks"] = "pre-submit check updates already started, by check id",
        ["submitting"] = "session submissions whose import activity is in flight",
        ["closePullRequests"] = "an abort asked for the close-pull-requests activity at the run's end",
        ["currentLock"] = "the run's lock reference after library overrides, carried in the next run's input",
    };

    /// <summary>Types from the interpreter the shell may hold in a field: the core, and wire records it is given or carries.</summary>
    static readonly IReadOnlyDictionary<Type, string> AllowedInterpreterTypes = new Dictionary<Type, string>
    {
        [typeof(RunCore)] = "the core itself",
        [typeof(RunPolicy)] = "the run's policy",
        [typeof(RunStart)] = "the workflow's input",
        [typeof(HandOver)] = "SP1's hand-over record, written by the shell when it continues-as-new",
        [typeof(ParentPatchSignal)] = "a signal's payload, held until the core exists",
        [typeof(TrackerUpdate)] = "a tracker update the core's TrackerUpdates found, waiting for its activity",
    };

    [Fact]
    public void The_shell_uses_no_routing_vocabulary()
    {
        var violations = Vocabulary(ShellSources());

        Assert.True(violations.Count == 0, $"{violations.Count} routing decision(s) in the shell:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void The_shell_holds_no_routing_state()
    {
        var violations = Fields(typeof(RunWorkflow));

        Assert.True(violations.Count == 0, $"{violations.Count} field problem(s) in the shell:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void The_vocabulary_rule_sees_a_shell_that_decides_for_itself()
    {
        // The rule's own check: each kind of decision the shell must not make is reported. B1 to B3 are the A2 review's
        // breaks that the first, name-only rule let through.
        var source = """
            partial class RunWorkflow
            {
                void M(Step step, AnswerArgs a, OverrideArgs o)
                {
                    if (step is { Form: StepForm.Action, Action: "run.child" }) { }
                    if (core.State.Waits.Any(w => w.Kind == WaitKind.Question)) { }
                    var r = a.Action == "retry";
                    var c = step?.Consumes;
                    if (a.WaitId.StartsWith("gate-") && a.Outcome is null) { }
                    if (core!.StepSpec(a.StepId).Spec?.What == "run" + ".child") { }
                    if (core!.State.Closed.Any(x => x.Id == a.WaitId && x.Key == RunCore.Key(a))) { }
                    core.State.Overrides.Add(new AppliedOverride(o.OverrideId, o.Key, "applied"));
                    core!.State.Trace.Add(new TraceEntry("override", "", "applied"));
                    if (core is { State.Status: RunStatus.Running }) { }
                    var held = core;
                    var kind = a.WaitId == "x" ? "patch_approval" : "breakpoint";
                }
            }
            """;

        var violations = Vocabulary([("Probe.cs", CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken))]);

        Assert.Contains(violations, v => v.Contains("StepForm"));
        Assert.Contains(violations, v => v.Contains("pattern Form"));
        Assert.Contains(violations, v => v.Contains("\"run.child\""));
        Assert.Contains(violations, v => v.Contains(".Waits"));
        Assert.Contains(violations, v => v.Contains("WaitKind"));
        Assert.Contains(violations, v => v.Contains("\"retry\""));
        Assert.Contains(violations, v => v.Contains(".Consumes"));
        // B1: a wait's kind decided from its id.
        Assert.Contains(violations, v => v.Contains(":9:") && v.Contains("literal \"gate-\""));
        Assert.Contains(violations, v => v.Contains(":9:") && v.Contains("the wait id's spelling"));
        // B2: an action name built by concatenation.
        Assert.Contains(violations, v => v.Contains(":10:") && v.Contains("literal \"run.child\""));
        // B3: a duplicate-answer ledger read from the core's state.
        Assert.Contains(violations, v => v.Contains(":11:") && v.Contains("core.State"));
        Assert.Contains(violations, v => v.Contains(":11:") && v.Contains("RunCore.Key"));
        // R1, R10: the core's state written by the shell.
        Assert.Contains(violations, v => v.Contains(":12:") && v.Contains("core.State"));
        Assert.Contains(violations, v => v.Contains(":13:") && v.Contains("core.State"));
        Assert.Contains(violations, v => v.Contains(":14:") && v.Contains("pattern on the core"));
        Assert.Contains(violations, v => v.Contains(":15:") && v.Contains("the core held as a value"));
        Assert.Contains(violations, v => v.Contains(":16:") && v.Contains("literal \"patch_approval\""));
        Assert.Contains(violations, v => v.Contains(":16:") && v.Contains("literal \"breakpoint\""));
    }

    /// <summary>Every file of the engine that declares <c>partial class RunWorkflow</c>, parsed (syntax only).</summary>
    static List<(string File, SyntaxTree Tree)> ShellSources()
    {
        var folder = Path.Combine(Repository.Root, "src", "FeatureFlow.Engine");
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal)
            .Select(f => (File: Path.GetRelativePath(Repository.Root, f), Tree: CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f, cancellationToken: TestContext.Current.CancellationToken)))
            .Where(t => t.Tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text == nameof(RunWorkflow)))
            .ToList();
        Assert.True(trees.Count >= 4, $"expected the shell's partial files, found {trees.Count}");
        return trees;
    }

    static List<string> Vocabulary(IEnumerable<(string File, SyntaxTree Tree)> sources)
    {
        var names = ActionRegistry.Default.All.Select(d => d.Name).Concat(Recoveries.All.Select(r => r.Name)).ToHashSet(StringComparer.Ordinal);
        // A wait kind's name as records write it and as wait ids spell it (patchapproval, patch_approval-3).
        var kinds = Enum.GetValues<WaitKind>().SelectMany(k => new[] { k.Name(), Snake(k.ToString()) }).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var (file, tree) in sources)
        {
            var shell = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == nameof(RunWorkflow));
            foreach (var node in shell.SelectMany(c => c.DescendantNodes()))
            {
                var found = node switch
                {
                    IdentifierNameSyntax { Identifier.Text: "core" } core => CoreUse(core),
                    IdentifierNameSyntax id when RoutingTypes.Contains(id.Identifier.Text) => $"type {id.Identifier.Text}",
                    MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: nameof(RunCore) } } s when !CoreMembers.ContainsKey(s.Name.Identifier.Text) =>
                        $"RunCore.{s.Name.Identifier.Text}, not a core member the shell may use",
                    MemberAccessExpressionSyntax m when RoutingMembers.Contains(m.Name.Identifier.Text) => $"member .{m.Name.Identifier.Text}",
                    MemberBindingExpressionSyntax b when RoutingMembers.Contains(b.Name.Identifier.Text) => $"member ?.{b.Name.Identifier.Text}",
                    SubpatternSyntax { NameColon.Name.Identifier.Text: var p } when RoutingMembers.Contains(p) => $"pattern {p}",
                    SubpatternSyntax { ExpressionColon.Expression: IdentifierNameSyntax e } when RoutingMembers.Contains(e.Identifier.Text) => $"pattern {e.Identifier.Text}",
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } when SpellingReads.Contains(call.Name.Identifier.Text) && IsWaitId(call.Expression) =>
                        $"the wait id's spelling read ({call.Name.Identifier.Text})",
                    ElementAccessExpressionSyntax index when IsWaitId(index.Expression) => "the wait id's spelling read (an index)",
                    ExpressionSyntax e when Constant(e) is { } text && !(e.Parent is ExpressionSyntax parent && Constant(parent) is not null) && Banned(text, names, kinds) is { } why =>
                        $"literal \"{text}\" ({why})",
                    _ => null,
                };
                if (found is not null)
                    violations.Add($"{file}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {found}");
            }
        }

        return violations;
    }

    /// <summary>
    /// Why a use of the shell's <c>core</c> field breaks the rule, or null: the core is read only through
    /// <see cref="CoreMembers"/>, compared with null, assigned, or given whole to one of <see cref="CoreTakers"/>.
    /// </summary>
    static string? CoreUse(IdentifierNameSyntax core)
    {
        SyntaxNode at = core.Parent is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang ? bang : core;
        string? member = at.Parent switch
        {
            MemberAccessExpressionSyntax m when m.Expression == at => m.Name.Identifier.Text,
            ConditionalAccessExpressionSyntax c when c.Expression == at => c.WhenNotNull.DescendantNodesAndSelf().OfType<MemberBindingExpressionSyntax>().First().Name.Identifier.Text,
            _ => null,
        };
        if (member is not null)
        {
            if (member == nameof(RunCore.State))
                return "core.State: the shell neither reads nor writes the core's state; add a core member that says what it needs";
            if (!CoreMembers.ContainsKey(member))
                return $"core.{member}, not a core member the shell may use";
            return ArgumentOnly.Contains(member) && at.Parent!.Parent is not ArgumentSyntax ? $"core.{member} read, not passed on" : null;
        }

        return at.Parent switch
        {
            IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax or UnaryPatternSyntax { Pattern: ConstantPatternSyntax } } => null,
            IsPatternExpressionSyntax => "a pattern on the core: read it through its members",
            BinaryExpressionSyntax { Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } => null,
            AssignmentExpressionSyntax a when a.Left == at => null,
            ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } when CoreTakers.Contains(call.Expression.ToString()) => null,
            _ => "the core held as a value or passed on: read it through its members",
        };
    }

    /// <summary>Whether an expression is a wait's or a question's id (<c>answer.WaitId</c>, <c>q.QuestionId</c>).</summary>
    static bool IsWaitId(ExpressionSyntax e) => (e switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => "",
    }) is var name && (name.EndsWith("WaitId", StringComparison.OrdinalIgnoreCase) || name.EndsWith("QuestionId", StringComparison.OrdinalIgnoreCase));

    /// <summary>The text of a constant string expression: a literal, an interpolation with no holes, or a concatenation of them.</summary>
    static string? Constant(SyntaxNode e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        InterpolatedStringExpressionSyntax s when s.Contents.All(c => c is InterpolatedStringTextSyntax) =>
            string.Concat(s.Contents.Cast<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
        ParenthesizedExpressionSyntax p => Constant(p.Expression),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && Constant(b.Left) is { } left && Constant(b.Right) is { } right => left + right,
        _ => null,
    };

    /// <summary>Why a constant string is routing vocabulary, or null: an action or recovery name, a wait kind's name, or a wait id's prefix.</summary>
    static string? Banned(string text, IReadOnlySet<string> names, IReadOnlySet<string> kinds) =>
        names.Contains(text) ? "an action or recovery name"
        : kinds.Contains(text) ? "a wait kind"
        : kinds.Any(k => text.StartsWith(k + "-", StringComparison.Ordinal)) ? "a wait id's prefix"
        : null;

    static string Snake(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    static List<string> Fields(Type shell)
    {
        var interpreter = typeof(RunCore).Assembly;
        var violations = new List<string>();
        var fields = shell.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var field in fields)
        {
            if (!TemporalFields.ContainsKey(field.Name))
                violations.Add($"field {field.Name} ({field.FieldType.Name}) is not listed as Temporal's: say why the core cannot hold it, or move it to the core");
            foreach (var type in Types(field.FieldType).Where(t => t.Assembly == interpreter && !AllowedInterpreterTypes.ContainsKey(t)))
                violations.Add($"field {field.Name} holds {type.Name}, the interpreter's; routing state belongs in RunState");
        }

        foreach (var name in TemporalFields.Keys.Where(n => fields.All(f => f.Name != n)))
            violations.Add($"field {name} is listed but no longer exists: remove it from the list");
        return violations;
    }

    /// <summary>A type and every type in its generic arguments and element type.</summary>
    static IEnumerable<Type> Types(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (var t in Types(type.GetElementType()!))
                yield return t;
        foreach (var t in type.GenericTypeArguments.SelectMany(Types))
            yield return t;
    }
}
