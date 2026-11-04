using System.Globalization;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation;

/// <summary>
/// The run-start checks of docs/09 section 2 that need no Temporal and no provider code: 19 (library
/// requirements), 20 (a repository's library section adds only), 21 (capability negotiation, given the
/// capabilities the configured providers offer, by name) and 22 (locked organisation caps). Check 18 is the
/// core's (<c>RunCore.Start</c>). The engine calls these with what its providers describe.
/// </summary>
public static class RunStartChecks
{
    /// <summary>The plan schema this engine reads (docs/08 section 2, <c>requires.plan_schema</c>).</summary>
    public const long PlanSchema = 1;

    /// <summary>
    /// Capability names a plan needs, by step form and action. A bare provider type means a provider of that type
    /// must be configured; <c>type.flag</c> means its capability record must have that flag (docs/06 section 1).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ActionNeeds = new Dictionary<string, IReadOnlyList<string>>
    {
        ["run.child"] = [],
        ["plan.expand"] = [],
        ["scm.catch_up"] = ["vcs", "code_host"],
        ["scm.pull_request"] = ["code_host.pull_request"],
        ["scm.merge"] = ["vcs", "code_host"],
        ["scm.revert"] = ["code_host.pull_request"],
        ["ci.checks"] = ["code_host.check_status", "ci"],
        ["wait.condition"] = ["code_host"],
        ["test.run"] = ["sandbox", "vcs", "code_host"],
        ["test.integration"] = ["sandbox", "vcs", "code_host"],
        ["analysis.run"] = ["sandbox", "vcs", "code_host"],
        ["artifact.validate"] = ["validator"],
        ["findings.record"] = [],
        ["findings.triage"] = [],
        ["repo.inspect"] = ["vcs", "code_host"],
        ["artifact.compose"] = [],
        ["artifact.import"] = ["vcs"],
        ["artifact.publish"] = ["vcs", "code_host.pull_request"],
        ["tracker.update"] = ["tracker"],
        ["notify.send"] = ["notifications"],
        ["delivery.report"] = ["code_host"],
    };

    /// <summary>What an agent step needs (docs/04 section 3): a sandbox, an agent runtime and a VCS provider.</summary>
    public static readonly IReadOnlyList<string> AgentNeeds = ["sandbox", "agent_runtime", "vcs"];

    /// <summary>What every run needs: the stores (docs/02 section 5).</summary>
    public static readonly IReadOnlyList<string> RunNeeds = ["artifact_store", "record_store"];

    /// <summary>The capabilities a plan needs, with the first step that needs each (null for the run or a library).</summary>
    public static IReadOnlyDictionary<string, string> Needs(Plan plan, LibrarySet libraries)
    {
        var needs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in RunNeeds)
            needs.TryAdd(n, "every run");
        foreach (var library in libraries.All.OrderBy(l => l.Name, StringComparer.Ordinal))
            foreach (var n in library.Manifest.RequiredCapabilities)
                needs.TryAdd(n, $"library {library.Name} (requires.capabilities)");
        foreach (var step in plan.AllSteps)
        {
            IEnumerable<string> stepNeeds = step.Form switch
            {
                StepForm.Agent => AgentNeeds,
                StepForm.Action when step.Action is { } a => ActionNeeds.GetValueOrDefault(a, []),
                _ => [],
            };
            if (step.Tracker is not null)
                stepNeeds = stepNeeds.Append("tracker");
            foreach (var n in stepNeeds)
                needs.TryAdd(n, $"step {step.Id}");
        }

        foreach (var use in plan.Instances.Where(i => i.Tracker is not null))
            needs.TryAdd("tracker", $"fragment use {use.Id} (tracker)");

        return needs;
    }

    /// <summary>Check 21: every capability the plan needs is offered by a configured provider.</summary>
    public static void Capabilities(Plan plan, LibrarySet libraries, IReadOnlySet<string> offered, DiagnosticBag d)
    {
        foreach (var (need, by) in Needs(plan, libraries))
            if (!offered.Contains(need))
            {
                var what = need.Contains('.')
                    ? $"capability {need} is not offered by the configured {need[..need.IndexOf('.')]} provider"
                    : $"no {need} provider is configured";
                d.Add(Checks.Capabilities, $"{what}; needed by {by}");
            }
    }

    /// <summary>
    /// Check 19's requirements: each library's <c>requires.engine</c> range holds for this engine's version and
    /// its <c>requires.plan_schema</c> is one this engine reads (docs/08 section 2).
    /// </summary>
    public static void LibraryRequirements(LibrarySet libraries, string engineVersion, DiagnosticBag d)
    {
        foreach (var library in libraries.All.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            var manifest = library.Manifest;
            if (manifest.RequiredEngine is { } range)
            {
                if (VersionRange.Parse(range) is not { } parsed)
                    d.Add(Checks.ReferenceResolves, $"library {library.Name}: requires.engine '{range}' is not a version range");
                else if (!parsed.Contains(engineVersion))
                    d.Add(Checks.ReferenceResolves, $"library {library.Name} requires engine {range}; this engine is {engineVersion}");
            }

            if (manifest.PlanSchema is { } schema && schema != PlanSchema)
                d.Add(Checks.ReferenceResolves, $"library {library.Name} requires plan schema {schema}; this engine reads {PlanSchema}");
        }
    }

    /// <summary>The keys a repository's <c>library</c> section may hold, and what <c>adds</c> may add (docs/08 section 4).</summary>
    public static readonly IReadOnlyList<string> RepositoryLibraryKeys = ["default_template", "adds"];

    public static readonly IReadOnlyList<string> RepositoryAdds = ["skills", "resources", "tasks"];

    /// <summary>
    /// Check 20: a repository's <c>library</c> section (from <c>feature-flow.yaml</c> on the default branch) names
    /// a default template and adds skills, resources and tasks under its own name. It never overrides: any other
    /// key, or an added item that has the name of an item in the run's libraries, is refused.
    /// </summary>
    public static void RepositoryLibrary(string repository, YNode? section, LibrarySet libraries, DiagnosticBag d)
    {
        if (section is null)
            return;
        if (section is not YMap map)
        {
            d.Add(Checks.RepositoryLibraryAddsOnly, $"{repository}: the library section must be a map", section.Location);
            return;
        }

        foreach (var (key, value) in map.Entries)
        {
            if (!RepositoryLibraryKeys.Contains(key.Text))
            {
                d.Add(Checks.RepositoryLibraryAddsOnly, $"{repository}: library.{key.Text} is not allowed; a repository may only name a default template and add content", key.Location);
                continue;
            }

            if (key.Text != "adds")
                continue;
            if (value is not YMap adds)
            {
                d.Add(Checks.RepositoryLibraryAddsOnly, $"{repository}: library.adds must be a map", value.Location);
                continue;
            }

            foreach (var (type, items) in adds.Entries)
            {
                if (!RepositoryAdds.Contains(type.Text))
                {
                    d.Add(Checks.RepositoryLibraryAddsOnly, $"{repository}: library.adds.{type.Text} is not allowed; a repository adds only {string.Join(", ", RepositoryAdds)}", type.Location);
                    continue;
                }

                foreach (var item in (items as YSeq)?.Items ?? [])
                {
                    var name = (item as YMap)?["name"].ScalarText();
                    if (name is null)
                        continue;
                    foreach (var library in libraries.All)
                        if (library.PathOf(type.Text, name) is not null)
                            d.Add(Checks.RepositoryLibraryAddsOnly, $"{repository}: library.adds.{type.Text} '{name}' has the name of {library.Name}:{type.Text}/{name}; a repository adds, never overrides", item.Location);
                }
            }
        }
    }

    /// <summary>
    /// Check 23, F1 (docs/05 section 2): a merge step into a repository's default branch may ask for an engine merge
    /// only where <paramref name="engineMayMerge"/> matches the repository. <paramref name="engineCanMerge"/> is null
    /// when it cannot be decided at run start; that is refused too, because a safety floor is not left to chance.
    /// </summary>
    public static void EngineMerge(string stepId, string repository, string defaultBranch, bool? engineCanMerge, IReadOnlyList<string> engineMayMerge,
        SourceLocation? location, DiagnosticBag d)
    {
        if (engineCanMerge == false || RepositoryPatterns.Any(engineMayMerge, repository))
            return;
        d.Add(Checks.EngineMerge, engineCanMerge is null
            ? $"{stepId} merges into {repository}'s default branch {defaultBranch}, and whether it asks for an engine merge cannot be decided at run start; set engine_can_merge from parameters"
            : $"{stepId} asks for an engine merge into {repository}'s default branch {defaultBranch}; the organisation does not allow it (policy.merge.default_branch.engine_may_merge). Set engine_can_merge: false so a person merges",
            location, stepId);
    }

    /// <summary>
    /// Check 24: the default branch of each repository the run targets is protected (docs/05 section 2, the final
    /// fallback). Any protection counts, classic or rulesets (LOG 147).
    /// </summary>
    public static void BranchProtection(string repository, string defaultBranch, bool isProtected, DiagnosticBag d)
    {
        if (!isProtected)
            d.Add(Checks.BranchProtection, $"{repository}'s default branch {defaultBranch} has no branch protection; protect it on the code host before running");
    }

    /// <summary>
    /// Check 22 at run start: no step exceeds a locked organisation cap (docs/05 section 3.1). Covered here: a
    /// step's attempt allowance above <c>policy.attempts.ceiling.per_step</c>; a decision asking for
    /// auto-resolution where the matching approval rule locks <c>auto</c> off; an <c>scm.merge</c> turning
    /// <c>require_checks</c> off where the organisation locks it on.
    /// </summary>
    /// <para>
    /// <paramref name="approval"/> finds the rule that governs a decision type (<see cref="Approval.For"/>); by default the
    /// organisation's rules. Where the rule does not allow auto-resolution and does not lock it off, the request is not
    /// refused but has no effect, and a warning says so (docs/05 section 3.1: validation reports where a request was capped).
    /// </para>
    public static void Caps(Plan plan, PolicyConfig policy, Func<Step, long> allowance, DiagnosticBag d, bool root = true, Func<string, EffectiveApproval>? approval = null)
    {
        approval ??= type => Approval.For(type, policy, null, []);
        // F11 is a floor: the organisation's ceiling is never exceeded, so it is locked whatever the file says. Only a root's
        // budget counts (LOG 187): a child's own is not read, so it is not checked.
        if (root && plan.Document.MaxWorkItems is { } asked && policy.MaxWorkItems is { } workItems && asked > workItems)
            d.Add(Checks.LockedCap, $"budget.max_work_items {asked.ToString(CultureInfo.InvariantCulture)} is above policy.budgets.max_work_items.ceiling {workItems.ToString(CultureInfo.InvariantCulture)} (F11)");
        foreach (var step in plan.AllSteps)
        {
            if (policy.AttemptsPerStep is { } ceiling && step.ConsumesAttempts && allowance(step) > ceiling)
                d.Add(Checks.LockedCap, $"{step.Id} allows {allowance(step).ToString(CultureInfo.InvariantCulture)} attempts, above policy.attempts.ceiling.per_step {ceiling.ToString(CultureInfo.InvariantCulture)} (F4)", step.Location, step.Id);
            if (step.Decision is { Auto: { }, Type: { } type } && approval(type) is { Rule.Auto.Value: false } governing)
            {
                if (governing.Rule.Auto.Locked)
                    d.Add(Checks.LockedCap, $"{step.Id} asks for auto-resolution of {type}; {governing.Source} locks auto off", step.Location, step.Id);
                else
                    d.AddRange([new Diagnostic(Checks.LockedCap, Severity.Warning,
                        $"{step.Id} asks for auto-resolution of {type}; {governing.Source} does not allow it, so a person answers", step.Location, step.Id)]);
            }
            if (step.Action == "scm.merge" && policy.RequireChecks is { Value: true, Locked: true }
                && step.With?["require_checks"] is YScalar { } s && s.AsBool() == false)
                d.Add(Checks.LockedCap, $"{step.Id} sets require_checks false; policy.merge.require_checks is locked true", step.Location, step.Id);
        }
    }
}

/// <summary>A version range of space-separated comparators, such as <c>&gt;=1.0.0 &lt;2.0.0</c>, over major.minor.patch.</summary>
public sealed record VersionRange(IReadOnlyList<(string Op, Version Version)> Comparators)
{
    public static VersionRange? Parse(string text)
    {
        var list = new List<(string, Version)>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = new[] { ">=", "<=", ">", "<", "=" }.FirstOrDefault(o => part.StartsWith(o, StringComparison.Ordinal)) ?? "=";
            if (!Version.TryParse(part[(part.StartsWith(op, StringComparison.Ordinal) ? op.Length : 0)..], out var v))
                return null;
            list.Add((op, v));
        }

        return list.Count == 0 ? null : new VersionRange(list);
    }

    public bool Contains(string version)
    {
        var plus = version.IndexOfAny(['+', '-']);
        if (!Version.TryParse(plus < 0 ? version : version[..plus], out var v))
            return false;
        return Comparators.All(c => c.Op switch
        {
            ">=" => v >= c.Version,
            "<=" => v <= c.Version,
            ">" => v > c.Version,
            "<" => v < c.Version,
            _ => v == c.Version,
        });
    }
}
