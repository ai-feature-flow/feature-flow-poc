using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Testing.Workflows;

/// <summary>What a step does on one visit: an outcome, or an error class first (docs/09 section 4.3); a gate's outcome may carry the person's reason.</summary>
public sealed record Visit(string? Outcome, string? Error = null, int Times = 1, string? Reason = null);

/// <summary>
/// A person's run-time override in a test (docs/05 section 5), applied at the run's start as an administrator would:
/// <c>breakpoint</c>, <c>skip</c> (with <c>outcome</c>) or <c>grant_attempts</c> (with <c>attempts</c>) on a step.
/// </summary>
public sealed record TestOverride(string Key, string StepId, string? Outcome = null, long Attempts = 0);

/// <summary>A person's answer to a breakpoint, intervention or patch approval in a test: a recovery action (docs/01 section 7.2), or <c>approve</c> or <c>reject</c>.</summary>
public sealed record TestAnswer(string Action, string? StepId = null, bool CleanUp = false, long Attempts = 0, string? Status = null);

/// <summary>
/// An agent's question while its step runs, in a test (docs/04 section 3.4): asked on a <see cref="Visit"/> of the step, then
/// answered with <see cref="Answer"/>, timed out (<see cref="TimesOut"/>), or with neither left open until the step ends.
/// </summary>
public sealed record TestQuestion(string Id, int Visit, string Ask, IReadOnlyList<string> Options, string? Answer, bool TimesOut);

/// <summary>A workflow test file (docs/09 section 4.3; LOG 75).</summary>
public sealed class WorkflowTest
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public string? Description { get; init; }
    public required LibraryReference Template { get; init; }
    public Dictionary<string, YNode> Parameters { get; } = new(StringComparer.Ordinal);
    public List<string> Repositories { get; } = [];

    /// <summary>Step id to outcome per visit. The last entry applies to every later visit.</summary>
    public Dictionary<string, List<Visit>> Script { get; } = new(StringComparer.Ordinal);

    /// <summary>Step id to outputs per visit.</summary>
    public Dictionary<string, List<YMap>> Outputs { get; } = new(StringComparer.Ordinal);

    /// <summary><c>findings.&lt;name&gt;</c> values, one per route evaluation that reads the name.</summary>
    public Dictionary<string, List<long>> Findings { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A <c>plan.expand</c> step id to the items of its list, one list per execution as <see cref="Script"/> has one outcome per
    /// visit; the last list applies to every later execution.
    /// </summary>
    public Dictionary<string, List<List<YNode>>> Expand { get; } = new(StringComparer.Ordinal);

    public PatchDocument? Patch { get; init; }

    public List<string>? ExpectPath { get; init; }
    public List<string>? ExpectPathIncludes { get; init; }
    public string? ExpectStatus { get; init; }
    public List<int> ExpectValidationErrors { get; } = [];
    public Dictionary<string, long> ExpectExecutions { get; } = new(StringComparer.Ordinal);
    public List<string>? ExpectEscalated { get; init; }

    /// <summary>The steps a <c>clean_up</c> route reset the branch for, in order.</summary>
    public List<string>? ExpectCleanUp { get; init; }

    /// <summary>A person's overrides, applied in order when the run starts.</summary>
    public List<TestOverride> Overrides { get; } = [];

    /// <summary>A person's answers to the breakpoints, interventions and patch approvals opened at a step, in the order they open.</summary>
    public Dictionary<string, List<TestAnswer>> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>Step id to the questions its agent asks, in order, each on its visit.</summary>
    public Dictionary<string, List<TestQuestion>> Questions { get; } = new(StringComparer.Ordinal);

    /// <summary>Question id to how it closed: <c>answered</c>, <c>timed_out</c> or <c>step_ended</c>.</summary>
    public Dictionary<string, string> ExpectQuestions { get; } = new(StringComparer.Ordinal);

    /// <summary>The steps a person's skip ended without running, in order.</summary>
    public List<string>? ExpectSkipped { get; init; }

    /// <summary>Agent step id to execution to the texts its run context must contain when that execution starts (docs/04 section 3.5).</summary>
    public Dictionary<string, Dictionary<int, List<string>>> ExpectContext { get; } = new(StringComparer.Ordinal);

    /// <summary>Step id to execution to keys it must have consumed, resolved, when that execution started (docs/03 section 7.2).</summary>
    public Dictionary<string, Dictionary<int, List<string>>> ExpectConsumed { get; } = new(StringComparer.Ordinal);

    /// <summary>A <c>parallel</c> or chain's id to its branches in the final plan, each by its first step's id, exactly and in order.</summary>
    public Dictionary<string, List<string>> ExpectBranches { get; } = new(StringComparer.Ordinal);
}

public static class WorkflowTestReader
{
    public static readonly IReadOnlyList<string> ErrorClasses = ["infrastructure", "work"];

    /// <summary>Reads a test file. Errors are check 1 diagnostics.</summary>
    public static WorkflowTest? Read(string path, DiagnosticBag d)
    {
        var file = Path.GetFileName(path);
        if (YamlText.Parse(System.IO.File.ReadAllText(path), file, d) is not { } node || MapReader.Of(node, d, "a workflow test") is not { } r)
            return null;
        if (r.String("api_version", required: true) is { } version and not "feature-flow/v1")
            r.Error($"api_version must be feature-flow/v1, not '{version}'", r.Map["api_version"]!.Location);
        if (r.String("kind", required: true) is { } kind and not "workflow_test")
            r.Error($"kind must be workflow_test, not '{kind}'", r.Map["kind"]!.Location);
        var name = r.String("name", required: true) ?? Path.GetFileNameWithoutExtension(path);
        var description = r.String("description");
        var templateText = r.String("template", required: true);
        var template = templateText is null ? null : LibraryReference.TryParse(templateText);
        if (templateText is not null && template is not { Type: "templates" })
            r.Error($"template must reference a template, such as std:templates/ticket_delivery, not '{templateText}'", r.Map["template"]!.Location);

        PatchDocument? patch = null;
        if (r.Node("patch") is { } patchNode)
            patch = PatchReader.Read(patchNode, d);

        var expect = MapReader.Of(r.Required("expect"), d, "expect");
        var test = new WorkflowTest
        {
            Name = name,
            File = path,
            Description = description,
            Template = template ?? new LibraryReference("std", "templates", "unknown"),
            Patch = patch,
            ExpectPath = expect?.Has("path") == true ? expect.StringList("path").ToList() : null,
            ExpectPathIncludes = expect?.Has("path_includes") == true ? expect.StringList("path_includes").ToList() : null,
            ExpectStatus = expect?.Enum("status", ["running", "completed", "failed", "rejected", "partial", "aborted", "incomplete"]),
            ExpectEscalated = expect?.Has("escalated") == true ? expect.StringList("escalated").ToList() : null,
            ExpectCleanUp = expect?.Has("clean_up") == true ? expect.StringList("clean_up").ToList() : null,
            ExpectSkipped = expect?.Has("skipped") == true ? expect.StringList("skipped").ToList() : null,
        };

        foreach (var item in r.Node("overrides") is { } overrides ? LibraryItemReader.Seq(overrides, d, "overrides") : [])
            if (ReadOverride(item, d) is { } o)
                test.Overrides.Add(o);

        if (MapReader.Of(r.Node("answers"), d, "answers") is { } answers)
            foreach (var (key, value) in answers.Map.Entries)
            {
                answers.Node(key.Text);
                var list = (value is YSeq seq ? seq.Items : [value]).Select(v => ReadAnswer(v, d)).OfType<TestAnswer>().ToList();
                if (list.Count > 0)
                    test.Answers[key.Text] = list;
            }

        if (MapReader.Of(r.Node("questions"), d, "questions") is { } questions)
            foreach (var (key, value) in questions.Map.Entries)
            {
                questions.Node(key.Text);
                var list = (value is YSeq seq ? seq.Items : [value]).Select((v, i) => ReadQuestion(v, key.Text, i + 1, d)).OfType<TestQuestion>().ToList();
                if (list.Count > 0)
                    test.Questions[key.Text] = list;
            }

        if (MapReader.Of(r.Node("parameters"), d, "parameters") is { } ps)
            foreach (var (key, value) in ps.Map.Entries)
            {
                ps.Node(key.Text);
                test.Parameters[key.Text] = value;
            }

        test.Repositories.AddRange(r.Has("repositories") ? r.StringList("repositories") : ["acme/api"]);

        if (MapReader.Of(r.Node("script"), d, "script") is { } script)
            foreach (var (key, value) in script.Map.Entries)
            {
                script.Node(key.Text);
                // A work error fails `times` executions, each a visit of its own, then `then` (LOG 75).
                var visits = (value is YSeq seq ? seq.Items : [value]).Select(v => ReadVisit(v, d)).OfType<Visit>()
                    .SelectMany(v => v.Error == "work"
                        ? Enumerable.Repeat(new Visit(null, "work"), v.Times).Concat(v.Outcome is null ? [] : [new Visit(v.Outcome)])
                        : [v])
                    .ToList();
                if (visits.Count > 0)
                    test.Script[key.Text] = visits;
            }

        if (MapReader.Of(r.Node("outputs"), d, "outputs") is { } outputs)
            foreach (var (key, value) in outputs.Map.Entries)
            {
                outputs.Node(key.Text);
                var entries = value is YSeq seq ? seq.Items : [value];
                if (key.Text.StartsWith("findings.", StringComparison.Ordinal))
                {
                    var counts = entries.Select(e => (e as YScalar)?.AsLong()).ToList();
                    if (counts.Any(c => c is null))
                        outputs.Error($"{key.Text} takes whole numbers, one per route evaluation that reads it", value.Location);
                    else
                        test.Findings[key.Text["findings.".Length..]] = counts.Select(c => c!.Value).ToList();
                    continue;
                }

                var maps = entries.OfType<YMap>().ToList();
                if (maps.Count != entries.Count)
                    outputs.Error($"outputs for {key.Text} are a map, or a list of maps, one per visit", value.Location);
                else
                    test.Outputs[key.Text] = maps;
            }

        if (MapReader.Of(r.Node("expand"), d, "expand") is { } expand)
            foreach (var (key, value) in expand.Map.Entries)
            {
                expand.Node(key.Text);
                // One list of items, or a list of such lists, one per execution.
                if (value is YSeq { Items.Count: > 0 } perExecution && perExecution.Items.All(i => i is YSeq))
                    test.Expand[key.Text] = perExecution.Items.Select(i => ((YSeq)i).Items.ToList()).ToList();
                else if (value is YSeq seq)
                    test.Expand[key.Text] = [seq.Items.ToList()];
                else
                    expand.Error($"expand.{key.Text} is the list plan.expand reads, or a list of them, one per execution", value.Location);
            }

        if (expect is not null)
        {
            if (expect.Node("validation_errors") is YSeq errors)
                foreach (var item in errors.Items)
                    if ((item as YScalar)?.AsLong() is { } check)
                        test.ExpectValidationErrors.Add((int)check);
                    else
                        expect.Error("validation_errors lists check numbers", item.Location);
            if (MapReader.Of(expect.Node("executions"), d, "expect.executions") is { } ex)
                foreach (var (key, _) in ex.Map.Entries)
                    if (ex.Int(key.Text) is { } count)
                        test.ExpectExecutions[key.Text] = count;
            if (MapReader.Of(expect.Node("questions"), d, "expect.questions") is { } eq)
                foreach (var (key, _) in eq.Map.Entries)
                    if (eq.Enum(key.Text, QuestionEnds, required: true) is { } how)
                        test.ExpectQuestions[key.Text] = how;
            if (MapReader.Of(expect.Node("context"), d, "expect.context") is { } ec)
                ReadByExecution(ec, "expect.context", "the texts its run context contains", test.ExpectContext, d);
            if (MapReader.Of(expect.Node("consumed"), d, "expect.consumed") is { } eco)
                ReadByExecution(eco, "expect.consumed", "the keys it consumed", test.ExpectConsumed, d);
            if (MapReader.Of(expect.Node("branches"), d, "expect.branches") is { } eb)
                foreach (var (key, _) in eb.Map.Entries)
                    test.ExpectBranches[key.Text] = eb.StringList(key.Text).ToList();
            expect.Finish();
        }

        r.Finish();
        return test;
    }

    /// <summary>Reads <c>{ &lt;step id&gt;: { &lt;execution&gt;: [texts] } }</c> into <paramref name="into"/>.</summary>
    static void ReadByExecution(MapReader m, string field, string what, Dictionary<string, Dictionary<int, List<string>>> into, DiagnosticBag d)
    {
        foreach (var (key, value) in m.Map.Entries)
        {
            m.Node(key.Text);
            if (MapReader.Of(value, d, $"{field}.{key.Text}") is not { } byExecution)
                continue;
            var expected = into[key.Text] = [];
            foreach (var (number, texts) in byExecution.Map.Entries)
            {
                byExecution.Node(number.Text);
                if (!int.TryParse(number.Text, out var execution) || execution < 1 || texts is not YSeq seq)
                    byExecution.Error($"{field}.{key.Text} maps an execution number to {what}", number.Location);
                else
                    expected[execution] = seq.Items.Select(i => (i as YScalar)?.Text ?? "").ToList();
            }

            byExecution.Finish();
        }
    }

    public static readonly IReadOnlyList<string> OverrideKeys = ["breakpoint", "skip", "grant_attempts"];

    static TestOverride? ReadOverride(YNode node, DiagnosticBag d)
    {
        if (MapReader.Of(node, d, "an override") is not { } m)
            return null;
        var key = OverrideKeys.FirstOrDefault(m.Has);
        if (key is null)
        {
            m.Error($"an override is one of {string.Join(", ", OverrideKeys)}, naming the step", m.Map.Location);
            return null;
        }

        var o = new TestOverride(key, m.String(key, required: true) ?? "", m.String("outcome", required: key == "skip"), m.Int("attempts") ?? 0);
        if (key == "grant_attempts" && o.Attempts <= 0)
            m.Error("grant_attempts needs attempts, a positive number", m.Map.Location);
        m.Finish();
        return o;
    }

    public static readonly IReadOnlyList<string> AnswerActions = Interpreter.Recoveries.For(Interpreter.WaitKind.Breakpoint);

    /// <summary>How a question closes (<c>Interpreter.WaitEnds</c>).</summary>
    public static readonly IReadOnlyList<string> QuestionEnds = [Interpreter.WaitEnds.Answered, Interpreter.WaitEnds.TimedOut, Interpreter.WaitEnds.StepEnded];

    static TestQuestion? ReadQuestion(YNode node, string stepId, int index, DiagnosticBag d)
    {
        if (MapReader.Of(node, d, "a question") is not { } m)
            return null;
        var q = new TestQuestion(m.String("id") ?? $"{stepId}-q{index}", (int)(m.Int("visit") ?? 1), m.String("ask", required: true) ?? "",
            m.Has("options") ? m.StringList("options").ToList() : [], m.String("answer"), m.Bool("timeout") ?? false);
        if (q.Answer is not null && q.TimesOut)
            m.Error("a question is answered or times out, not both", m.Map.Location);
        if (q.Visit < 1)
            m.Error("visit counts from 1", m.Map.Location);
        m.Finish();
        return q;
    }

    static TestAnswer? ReadAnswer(YNode node, DiagnosticBag d)
    {
        if (node is YScalar { IsNull: false } s)
        {
            if (s.Text is "continue" or "retry" or "approve" or "reject")
                return new TestAnswer(s.Text);
            d.Add(Checks.Schema, $"an answer is continue, retry, approve, reject, or a map such as {{ go_to: <step> }}, not '{s.Text}'", s.Location);
            return null;
        }

        if (MapReader.Of(node, d, "an answer") is not { } m)
            return null;
        var action = AnswerActions.FirstOrDefault(m.Has);
        TestAnswer? answer = action switch
        {
            "go_to" or "replan" => new TestAnswer(action, m.String(action, required: true), m.Bool("clean_up") ?? false),
            "grant_attempts" => new TestAnswer(action, Attempts: m.Int(action) ?? 0),
            "end" => new TestAnswer(action, Status: m.String(action, required: true)),
            "continue" or "retry" => new TestAnswer(action),
            _ => null,
        };
        if (answer is null)
            m.Error($"an answer is one of {string.Join(", ", AnswerActions)}", m.Map.Location);
        else if (action is "continue" or "retry")
            m.Node(action);
        m.Finish();
        return answer;
    }

    static Visit? ReadVisit(YNode node, DiagnosticBag d)
    {
        if (node is YScalar { IsNull: false } s)
            return new Visit(s.Text);
        if (MapReader.Of(node, d, "a script entry") is not { } m)
            return null;
        if (m.Has("outcome"))
        {
            // A person's answer at a gate, with their reason.
            var answer = new Visit(m.String("outcome", required: true), Reason: m.String("reason"));
            m.Finish();
            return answer;
        }

        var error = m.Enum("error", ErrorClasses, required: true);
        var times = m.Int("times") ?? 1;
        var then = m.String("then");
        m.Finish();
        return new Visit(then, error, (int)times);
    }
}
