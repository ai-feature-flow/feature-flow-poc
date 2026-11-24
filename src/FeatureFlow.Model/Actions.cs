using System.Globalization;

namespace FeatureFlow.Model;

/// <summary>The class of an outcome (docs/03 section 3). <see cref="None"/> is <c>~</c>: no default route.</summary>
public enum OutcomeClass
{
    None,
    Success,
    Failure,
    Blocked,
}

public static class OutcomeClasses
{
    public static OutcomeClass? Parse(string? text) => text switch
    {
        null => OutcomeClass.None,
        "success" => OutcomeClass.Success,
        "failure" => OutcomeClass.Failure,
        "blocked" => OutcomeClass.Blocked,
        _ => null,
    };

    public static string Name(this OutcomeClass c) => c switch
    {
        OutcomeClass.Success => "success",
        OutcomeClass.Failure => "failure",
        OutcomeClass.Blocked => "blocked",
        _ => "~",
    };
}

public sealed record Outcome(string Name, OutcomeClass Class);

/// <summary>Who carries an action out: the engine's handler for it, or the run itself (a child run, or a patch it applies).</summary>
public enum ActionHandling
{
    Engine,
    ChildRun,
    Expand,
}

/// <summary>
/// The pull request an action opens: the run's work in a repository, which later actions there act on (<c>scm.pull_request</c>),
/// or a publication of documents (<c>artifact.publish</c>). An abort closes both.
/// </summary>
public enum PullRequestKind
{
    Work,
    Publication,
}

/// <summary>
/// One input of an action: its kind (<c>string</c>, <c>boolean</c>, <c>array</c>, <c>duration</c>, <c>map</c>), for a
/// string the values allowed, for a duration the least it may be, and for a library reference the item type it names.
/// </summary>
public sealed record ActionInput(string Name, string Kind, IReadOnlyList<string>? Allowed = null, TimeSpan? Minimum = null)
{
    /// <summary>The library item type the input names (<c>templates</c>, <c>prompts</c>); null when it is not a reference.</summary>
    public string? References { get; init; }

    /// <summary>The capabilities a step needs when it sets this input, beyond the action's own (check 21; LOG 489).</summary>
    public IReadOnlyList<string> Needs { get; init; } = [];

    /// <summary>
    /// Whether the input must be written as a literal with a value: an expression, an artifact, a null or an empty value is
    /// refused at load (check 31), because something other than the step reads it from the plan, as the API reads
    /// <c>findings.triage</c>'s <c>complete</c> and <c>person_decides</c>.
    /// </summary>
    public bool Literal { get; init; }
}

/// <summary>An output an action publishes, with its type (docs/03 section 5.4).</summary>
public sealed record ActionOutput(string Name, string Type);

/// <summary>
/// Everything the engine knows about one action, in one place (docs/04 section 2): outcomes with classes, the outputs it
/// publishes (null when docs/04 does not list them: any name, typed <c>dyn</c>), its input schema (check 31), the
/// capabilities it needs (check 21) and who carries it out. A table keyed by action name anywhere else is a second copy.
/// </summary>
public sealed record ActionDescriptor(
    string Name,
    IReadOnlyList<Outcome> Outcomes,
    IReadOnlyList<ActionOutput>? Outputs,
    IReadOnlyList<ActionInput> Inputs,
    IReadOnlyList<string> Needs,
    ActionHandling Handling = ActionHandling.Engine)
{
    /// <summary>The evidence type the action makes, indexed when the step does not declare it (LOG 180); null for none.</summary>
    public string? Evidence { get; init; }

    /// <summary>Whether the action may wait on the outside world between reads, so a hand-over interrupts it (LOG 139).</summary>
    public bool WaitsBetweenReads { get; init; }

    /// <summary>The suffix of the gate type the action raises while it runs (<c>&lt;level&gt;.merge_action</c>, LOG 331); null for none.</summary>
    public string? RaisesGate { get; init; }

    /// <summary>Whether the action merges: a lost race (<c>base_moved</c>) counts against <c>limits.races</c>, not attempts (LOG 61).</summary>
    public bool CountsRaces { get; init; }

    /// <summary>The pull request the action opens, published as its <c>pull_request</c> output; null for none (LOG 385).</summary>
    public PullRequestKind? OpensPullRequest { get; init; }

    /// <summary>Whether the artifact the action makes is an artifact set, whose members a validator reads (LOG 385).</summary>
    public bool MakesSet { get; init; }

    /// <summary>A rule across the inputs, given the evaluated values; checked at step start, after each input's own kind.</summary>
    public Func<IReadOnlyDictionary<string, object?>, IEnumerable<string>>? Rule { get; init; }

    /// <summary>The inputs that name library items, rendered or resolved by the engine.</summary>
    public IReadOnlyList<string> ReferenceInputs => Inputs.Where(i => i.References is not null).Select(i => i.Name).ToList();

    /// <summary>The type of a published output: <c>dyn</c> when docs/04 lists none, null when the action does not publish it.</summary>
    public string? OutputType(string name) => Outputs is null ? "dyn" : Outputs.FirstOrDefault(o => o.Name == name)?.Type;

    /// <summary>
    /// The capabilities a step of the action needs, given the inputs it sets (check 21): the action's own, and each set
    /// input's, such as <c>wait.condition</c>'s tracker condition needing a tracker (LOG 489).
    /// </summary>
    public IEnumerable<string> NeedsFor(IEnumerable<string> setInputs)
    {
        var set = setInputs.ToHashSet(StringComparer.Ordinal);
        return Needs.Concat(Inputs.Where(i => set.Contains(i.Name)).SelectMany(i => i.Needs)).Distinct(StringComparer.Ordinal);
    }

    /// <summary>The gate type the action raises at a run's level, or null.</summary>
    public string? GateType(string level) => RaisesGate is null ? null : $"{level}.{RaisesGate}";

    /// <summary>
    /// Check 31: what is wrong with the action's inputs, as messages; empty when they satisfy its schema. A null value is not
    /// checked (an unset input, or an expression not yet evaluated); <paramref name="rule"/> false leaves out <see cref="Rule"/>,
    /// which needs every input evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors(IReadOnlyDictionary<string, object?> inputs, bool rule = true)
    {
        var errors = new List<string>();
        foreach (var (name, value) in inputs)
        {
            if (Inputs.FirstOrDefault(i => i.Name == name) is not { } input)
            {
                errors.Add($"{Name} has no input '{name}'; it takes {string.Join(", ", Inputs.Select(i => i.Name))}");
                continue;
            }

            if (value is null)
                continue;
            var ok = input.Kind switch
            {
                "string" => value is string,
                "boolean" => value is bool,
                "array" => value is IList<object?>,
                "map" => value is IDictionary<string, object?>,
                "duration" => value is string d && Durations.Parse(d) is not null,
                _ => true,
            };
            if (!ok)
                errors.Add($"{Name} input '{name}' must be {input.Kind switch { "duration" => "a duration such as 30s, 5m or 2h", "boolean" => "true or false", "array" => "a list", _ => "a " + input.Kind }}, not {Describe(value)}");
            else if (input.Literal && value is "" or IList<object?> { Count: 0 } or IDictionary<string, object?> { Count: 0 })
                errors.Add($"{Name} input '{name}' must not be empty{(input.Allowed is { } named ? $"; it names one or more of {string.Join(", ", named)}" : "")}");
            else if (input.Allowed is { } allowed && value is string text && !allowed.Contains(text))
                errors.Add($"{Name} input '{name}' must be one of {string.Join(", ", allowed)}, not '{text}'");
            else if (input.Allowed is { } names && value is IList<object?> items && items.FirstOrDefault(x => x is not string s || !names.Contains(s)) is { } other)
                errors.Add($"{Name} input '{name}' must name one or more of {string.Join(", ", names)}, not {(other is string s ? $"'{s}'" : Describe(other))}");
            else if (input.Minimum is { } minimum && value is string d && Durations.Parse(d) < minimum)
                errors.Add($"{Name} input '{name}' must be at least {minimum.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s, not {d}");
        }

        if (rule && Rule is not null)
            errors.AddRange(Rule(inputs));
        return errors;
    }

    static string Describe(object value) => value switch
    {
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        IDictionary<string, object?> => "a map",
        IList<object?> => "a list",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "a value",
    };
}

/// <summary>
/// The actions a run may use: <see cref="Default"/>, the twenty of docs/04 section 2, or a test's own set for the duration of a
/// <see cref="Use"/> scope (an <see cref="AsyncLocal{T}"/>, so it does not cross into Temporal's worker threads).
/// </summary>
public sealed class ActionRegistry
{
    static readonly AsyncLocal<ActionRegistry?> Scoped = new();

    readonly Dictionary<string, ActionDescriptor> byName;

    ActionRegistry(IEnumerable<ActionDescriptor> descriptors) => byName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static ActionRegistry Default { get; } = new(Actions.Catalogue());

    public static ActionRegistry Current => Scoped.Value ?? Default;

    /// <summary>Makes <paramref name="registry"/> current until the scope is disposed. For tests.</summary>
    public static IDisposable Use(ActionRegistry registry)
    {
        var previous = Scoped.Value;
        Scoped.Value = registry;
        return new Restore(() => Scoped.Value = previous);
    }

    public ActionDescriptor? Get(string name) => byName.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, ActionDescriptor> ByName => byName;

    public IEnumerable<ActionDescriptor> All => byName.Values;

    /// <summary>This registry with one more action, or one replaced.</summary>
    public ActionRegistry With(ActionDescriptor extra) => new(byName.Values.Where(d => d.Name != extra.Name).Append(extra));

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>The engine actions of docs/04 section 2, and what the engine publishes for agent steps.</summary>
public static class Actions
{
    static Outcome S(string n) => new(n, OutcomeClass.Success);
    static Outcome F(string n) => new(n, OutcomeClass.Failure);
    static Outcome B(string n) => new(n, OutcomeClass.Blocked);

    static ActionInput Str(string name, params string[] allowed) => new(name, "string", allowed.Length == 0 ? null : allowed);
    static ActionInput Bool(string name) => new(name, "boolean");
    static ActionInput List(string name) => new(name, "array");
    static ActionInput Map(string name) => new(name, "map");
    static ActionInput Time(string name, TimeSpan? minimum = null) => new(name, "duration", Minimum: minimum);
    static ActionOutput Number(string name) => new(name, "number");
    static ActionOutput Text(string name) => new(name, "string");
    static ActionOutput Flag(string name) => new(name, "boolean");
    static ActionOutput Items(string name) => new(name, "array");

    /// <summary>The shortest poll interval a plan may ask for, so a wait cannot hammer a provider (it must not spin).</summary>
    public static readonly TimeSpan MinimumPoll = TimeSpan.FromSeconds(5);

    /// <summary>A finding's states (docs/01 section 6), which <c>findings.triage</c>'s <c>states</c> chooses from.</summary>
    static readonly string[] FindingStates = ["open", "fixed", "declined", "escalated"];

    /// <summary>The actions of docs/04 section 2: the contents of <see cref="ActionRegistry.Default"/>.</summary>
    internal static IEnumerable<ActionDescriptor> Catalogue() =>
    [
        new("run.child", [S("completed"), F("partial"), F("rejected"), F("failed")], [],
            [Str("template") with { References = "templates" }, Map("parameters"), List("repositories"), Str("tracker_issue")], [], ActionHandling.ChildRun),
        new("plan.expand", [S("expanded")], [],
            [Str("from"), Str("shape", "parallel", "chain"), Str("target"), Str("on_other"), Map("step")], [], ActionHandling.Expand),
        new("scm.catch_up", [S("updated"), B("conflict")], [new("changed", "boolean")], [Str("repository")], ["vcs", "code_host"]),
        new("scm.pull_request", [S("ready")], [new("pull_request", "string")],
            [Str("title"), Str("body") with { References = "prompts" }, Bool("draft"), Bool("post_declines"), Bool("post_review")], ["code_host.pull_request"])
        {
            OpensPullRequest = PullRequestKind.Work,
        },
        new("scm.merge", [S("merged"), B("base_moved"), F("checks_failed"), F("declined")], [],
            [Bool("engine_can_merge"), Bool("squash"), Bool("require_checks"), Bool("require_tested")], ["vcs", "code_host"])
        {
            Evidence = "merge_record", WaitsBetweenReads = true, RaisesGate = "merge_action", CountsRaces = true,
        },
        new("scm.revert", [S("opened")], [], [Str("pull_request")], ["code_host.pull_request"]) { Evidence = "revert_record" },
        new("ci.checks", [S("passed"), F("failed")], [Number("skipped")],
            [List("expected"), Time("grace"), Time("timeout"), Time("poll_every", MinimumPoll)], ["code_host.check_status", "ci"])
        {
            Evidence = "check_results", WaitsBetweenReads = true,
        },
        new("wait.condition", [S("met"), F("unmet")], [],
            [
                Str("pull_request", "approved", "merged", "mergeable") with { Needs = ["code_host"] },
                Str("checks", "passed") with { Needs = ["code_host.check_status"] },
                Map("tracker") with { Needs = ["tracker"] },
                Time("poll_every", MinimumPoll), Time("timeout"),
            ],
            [])
        {
            WaitsBetweenReads = true, Rule = OneCondition,
        },
        new("test.run", [S("passed"), F("failed"), B("baseline_failed")], null, [List("tier_tags"), Bool("baseline")], ["sandbox", "vcs", "code_host"])
        {
            Evidence = "test_results",
        },
        new("test.integration", [S("passed"), F("failed")], null, [List("repositories"), List("tier_tags")], ["sandbox", "vcs", "code_host"])
        {
            Evidence = "test_results",
        },
        new("analysis.run", [S("passed"), F("failed")], null,
            [Bool("baseline"), Str("fail_on", "new_issues", "any_issues", "never"), Str("min_level", "note", "warning", "error")], ["sandbox", "vcs", "code_host"])
        {
            Evidence = "analysis_results",
        },
        new("artifact.validate", [S("valid"), F("invalid")], null, [List("validation")], ["validator"]) { Evidence = "validation_report" },
        new("findings.record", [S("recorded"), B("unavailable")], [Number("new"), Number("reraised"), Number("reopened"), Number("repeat_fixes")],
            [Str("source"), Str("reopen_on", "code_change", "always", "never")], []),
        new("findings.triage", [S("applied"), F("invalid")], [Number("escalated"), Number("declined_blocking"), Number("declined_demonstrated")],
            [Str("from"), Bool("post_declines"), List("states"), Bool("complete") with { Literal = true }, new ActionInput("person_decides", "array", FindingClasses.All) { Literal = true }], [])
        {
            Rule = TriageStates,
        },
        new("repo.inspect", [S("inspected")],
            [Items("tiers"), Items("tags"), Flag("ci"), Text("analyser"), Flag("integration_environment"), Text("manifest_hash")],
            [Str("repository")], ["vcs", "code_host"]),
        new("artifact.compose", [S("composed")], null, [Str("name"), List("members")], []) { MakesSet = true },
        new("artifact.import", [S("imported")], null, [Str("repository"), Str("folder"), List("files"), Str("commit")], ["code_host"]),
        new("artifact.publish", [S("published"), B("conflict")], null,
            [Str("repository"), Str("folder"), List("files"), Str("previous"), Str("title"), Str("body")], ["vcs", "code_host.pull_request"])
        {
            OpensPullRequest = PullRequestKind.Publication,
        },
        new("tracker.update", [S("done"), F("not_found")], [Text("issue")],
            [Str("op", "create", "update", "transition", "link", "comment"), Str("issue"), Str("title"), Str("body"), List("labels"), Str("parent"),
                Map("fields"), Str("project"), Str("status"), Str("to"), Str("link_type")], ["tracker"]),
        new("delivery.report", [S("complete"), F("partial")], [Items("merged"), Items("not_merged")], [List("repositories")], ["code_host"]),
    ];

    static IEnumerable<string> OneCondition(IReadOnlyDictionary<string, object?> inputs)
    {
        var conditions = new[] { "pull_request", "checks", "tracker" }.Count(c => inputs.GetValueOrDefault(c) is not null);
        if (conditions != 1)
            yield return $"wait.condition takes exactly one condition of pull_request, checks or tracker, not {conditions.ToString(CultureInfo.InvariantCulture)}";
    }

    static IEnumerable<string> TriageStates(IReadOnlyDictionary<string, object?> inputs)
    {
        if (inputs.GetValueOrDefault("states") is IList<object?> states
            && (states.Count == 0 || states.Any(x => x is not string state || !FindingStates.Contains(state))))
            yield return $"findings.triage input 'states' must name one or more of {string.Join(", ", FindingStates)}";
    }

    /// <summary>The current registry's actions by name.</summary>
    public static IReadOnlyDictionary<string, ActionDescriptor> All => ActionRegistry.Current.ByName;

    /// <summary>An action of the current registry, or null.</summary>
    public static ActionDescriptor? Get(string? name) => name is null ? null : ActionRegistry.Current.Get(name);

    /// <summary>The actions that make evidence of a type (by its name, such as <c>test_results</c>).</summary>
    public static IReadOnlySet<string> Producing(string evidence) =>
        ActionRegistry.Current.All.Where(d => d.Evidence == evidence).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>The outputs the engine publishes for every agent step (docs/04 section 3.6).</summary>
    public static readonly IReadOnlyList<ActionOutput> AgentEngineOutputs =
        [new("coverage_known", "boolean"), new("changed_files_unread", "number"), new("commits", "number")];

    /// <summary>Added to every step that consumes attempts; routed to escalate by default (docs/03 section 4.3).</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>Outcomes of a <c>parallel</c> or a <c>for_each</c> (docs/03 section 8.1).</summary>
    public static readonly IReadOnlyList<Outcome> JoinOutcomes = [S("completed"), F("any_failed")];
}

/// <summary>
/// The classes of finding an agent may not clear alone where the workflow says a person decides them (docs/04 section
/// 11.2): <c>findings.triage</c>'s <c>person_decides</c> names some of them, and its outputs count each.
/// </summary>
public static class FindingClasses
{
    public const string Escalated = "escalated";
    public const string DeclinedBlocking = "declined_blocking";
    public const string DeclinedDemonstrated = "declined_demonstrated";

    public static readonly IReadOnlyList<string> All = [Escalated, DeclinedBlocking, DeclinedDemonstrated];

    /// <summary>What a <c>findings.triage</c> step that leaves <c>person_decides</c> unset decides: all three.</summary>
    public static readonly IReadOnlySet<string> Default = All.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The one reader of a <c>findings.triage</c> step's person policy, used by the engine on the step's evaluated inputs and
    /// by the API on the plan's literals (<c>ToPlain</c>), so the two cannot disagree: null when <c>complete</c> is not
    /// <c>true</c> (the step decides nothing for a person); otherwise the classes <c>person_decides</c> names, or
    /// <see cref="Default"/> when it is unset. Both inputs are literal (check 31 refuses an expression, null or empty value).
    /// </summary>
    public static IReadOnlySet<string>? PersonDecides(object? complete, object? personDecides) =>
        complete is not true ? null
        : personDecides is IList<object?> list ? list.OfType<string>().ToHashSet(StringComparer.Ordinal)
        : Default;
}
