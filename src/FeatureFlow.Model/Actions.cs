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

/// <summary>
/// One engine action's fixed contract as far as loading needs it: outcomes with classes, the outputs it
/// publishes (null when docs/04 does not list them), and the inputs that hold library references.
/// <see cref="Escalates"/> are outcomes added after libraries were written: one a plan routes nowhere escalates, as
/// <c>attempts_exhausted</c> does, so a library (or a run's locked library) from before the addition still loads (LOG 161).
/// </summary>
public sealed record ActionContract(string Name, IReadOnlyList<Outcome> Outcomes, IReadOnlyList<string>? Outputs, IReadOnlyList<string> ReferenceInputs,
    IReadOnlyList<string>? Escalates = null);

/// <summary>The twenty-one engine actions of docs/04 section 2.</summary>
public static class Actions
{
    static Outcome S(string n) => new(n, OutcomeClass.Success);
    static Outcome F(string n) => new(n, OutcomeClass.Failure);
    static Outcome B(string n) => new(n, OutcomeClass.Blocked);

    public static readonly IReadOnlyDictionary<string, ActionContract> All = new[]
    {
        new ActionContract("run.child", [S("completed"), F("partial"), F("rejected"), F("failed")], [], ["template"]),
        new ActionContract("plan.expand", [S("expanded")], [], []),
        new ActionContract("scm.catch_up", [S("updated"), B("conflict")], ["changed"], []),
        new ActionContract("scm.pull_request", [S("ready")], ["pull_request"], ["body"]),
        new ActionContract("scm.merge", [S("merged"), B("base_moved"), F("checks_failed"), F("declined")], [], []),
        new ActionContract("scm.revert", [S("opened")], [], []),
        new ActionContract("ci.checks", [S("passed"), F("failed")], ["skipped"], []),
        new ActionContract("wait.condition", [S("met"), F("unmet")], [], []),
        new ActionContract("test.run", [S("passed"), F("failed"), B("baseline_failed")], null, []),
        new ActionContract("test.integration", [S("passed"), F("failed")], null, []),
        new ActionContract("analysis.run", [S("passed"), F("failed")], null, []),
        new ActionContract("artifact.validate", [S("valid"), F("invalid")], null, []),
        new ActionContract("findings.record", [S("recorded"), B("unavailable")], ["new", "reraised", "reopened", "repeat_fixes"], []),
        new ActionContract("findings.triage", [S("applied"), F("invalid")], ["escalated", "declined_blocking"], [], Escalates: ["invalid"]),
        new ActionContract("repo.inspect", [S("inspected")], null, []),
        new ActionContract("artifact.compose", [S("composed")], null, []),
        new ActionContract("artifact.import", [S("imported")], null, []),
        new ActionContract("artifact.publish", [S("published"), B("conflict")], null, []),
        new ActionContract("tracker.update", [S("done"), F("not_found")], null, []),
        new ActionContract("notify.send", [S("sent")], null, []),
        new ActionContract("delivery.report", [S("complete"), F("partial")], null, []),
    }.ToDictionary(a => a.Name);

    /// <summary>The outputs the engine publishes for every agent step (docs/04 section 3.6).</summary>
    public static readonly IReadOnlyList<string> AgentEngineOutputs = ["coverage_known", "changed_files_unread", "commits"];

    /// <summary>
    /// The types of the outputs the engine publishes: for agent steps (docs/04 section 3.6) and for the
    /// actions whose outputs docs/04 lists. Types are those of docs/03 section 5.4.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EngineOutputTypes = new Dictionary<string, string>
    {
        ["coverage_known"] = "boolean",
        ["changed_files_unread"] = "number",
        ["commits"] = "number",
        ["changed"] = "boolean",
        ["skipped"] = "number",
        ["pull_request"] = "string",
        ["new"] = "number",
        ["reraised"] = "number",
        ["reopened"] = "number",
        ["repeat_fixes"] = "number",
        ["escalated"] = "number",
        ["declined_blocking"] = "number",
    };

    /// <summary>Added to every step that consumes attempts; routed to escalate by default (docs/03 section 4.3).</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>Outcomes of a <c>parallel</c> or a <c>for_each</c> (docs/03 section 8.1).</summary>
    public static readonly IReadOnlyList<Outcome> JoinOutcomes = [S("completed"), F("any_failed")];
}
