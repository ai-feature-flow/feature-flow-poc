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
/// no routing vocabulary in the shell's source, and no routing state in its fields.
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
        ["activitiesStartedWhileDraining"] = "drain: counted for this run's hand-over record (LOG A2-0i)",
        ["childEndsWhileDraining"] = "drain: counted for this run's hand-over record (LOG A2-0i)",
        ["answersWhileDraining"] = "drain: counted for this run's hand-over record (LOG A2-0i)",
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
        // The rule's own check: each kind of decision the shell must not make is reported.
        var source = """
            partial class RunWorkflow
            {
                void M(Step step, AnswerArgs a)
                {
                    if (step is { Form: StepForm.Action, Action: "run.child" }) { }
                    if (core.State.Waits.Any(w => w.Kind == WaitKind.Question)) { }
                    var r = a.Action == "retry";
                    var c = step?.Consumes;
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
        var violations = new List<string>();
        foreach (var (file, tree) in sources)
        {
            var shell = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == nameof(RunWorkflow));
            foreach (var node in shell.SelectMany(c => c.DescendantNodes()))
            {
                var found = node switch
                {
                    IdentifierNameSyntax id when RoutingTypes.Contains(id.Identifier.Text) => $"type {id.Identifier.Text}",
                    MemberAccessExpressionSyntax m when RoutingMembers.Contains(m.Name.Identifier.Text) => $"member .{m.Name.Identifier.Text}",
                    MemberBindingExpressionSyntax b when RoutingMembers.Contains(b.Name.Identifier.Text) => $"member ?.{b.Name.Identifier.Text}",
                    SubpatternSyntax { NameColon.Name.Identifier.Text: var p } when RoutingMembers.Contains(p) => $"pattern {p}",
                    SubpatternSyntax { ExpressionColon.Expression: IdentifierNameSyntax e } when RoutingMembers.Contains(e.Identifier.Text) => $"pattern {e.Identifier.Text}",
                    LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) && names.Contains(l.Token.ValueText) => $"literal \"{l.Token.ValueText}\"",
                    _ => null,
                };
                if (found is not null)
                    violations.Add($"{file}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {found}");
            }
        }

        return violations;
    }

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
