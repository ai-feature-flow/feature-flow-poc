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
    /// What an agent step needs (docs/04 section 3): a sandbox, a VCS provider and an agent runtime, and of the runtime its
    /// role maps to the hook reading (the step's tool calls are held and recorded through hooks) and the MCP server (the
    /// engine's tools).
    /// </summary>
    public static readonly IReadOnlyList<string> AgentNeeds = ["sandbox", "vcs", "agent_runtime", "agent_runtime.hooks", "agent_runtime.mcp"];

    /// <summary>What every run needs: the stores (docs/02 section 5).</summary>
    public static readonly IReadOnlyList<string> RunNeeds = ["artifact_store", "record_store"];

    /// <summary>One need of a plan: the capability, who needs it, and whether an agent step does, with its role (which picks the runtime instance).</summary>
    public sealed record Need(string Name, string By, bool AgentStep = false, string? Role = null)
    {
        /// <summary>The provider type the need is of: a <c>type.flag</c> need implies its type.</summary>
        public string Type => Name.Contains('.') ? Name[..Name.IndexOf('.')] : Name;
    }

    /// <summary>
    /// The capabilities a plan needs, with the first step that needs each (null for the run or a library). An action's are
    /// its descriptor's, given the inputs the step sets (<see cref="ActionDescriptor.NeedsFor"/>): a bare provider type means a provider of that type must be
    /// configured; <c>type.flag</c> means its capability record must have that flag (docs/06 section 1).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Needs(Plan plan, LibrarySet libraries)
    {
        var needs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var need in AllNeeds(plan, libraries))
            needs.TryAdd(need.Name, need.By);
        return needs;
    }

    /// <summary>Every need of the plan, in order: the run's, each library's, then each step's.</summary>
    static IEnumerable<Need> AllNeeds(Plan plan, LibrarySet libraries)
    {
        foreach (var n in RunNeeds)
            yield return new Need(n, "every run");
        foreach (var library in libraries.All.OrderBy(l => l.Name, StringComparer.Ordinal))
            foreach (var n in library.Manifest.RequiredCapabilities)
                yield return new Need(n, $"library {library.Name} (requires.capabilities)");
        foreach (var step in plan.AllSteps)
        {
            IEnumerable<string> stepNeeds = step.Form switch
            {
                StepForm.Agent => AgentNeeds,
                StepForm.Action => step.Descriptor?.NeedsFor(step.With?.Entries.Where(e => e.Value is not YScalar { IsNull: true }).Select(e => e.Key.Text) ?? []) ?? [],
                _ => [],
            };
            if (step.Tracker is not null)
                stepNeeds = stepNeeds.Append("tracker");
            var agent = step.Form == StepForm.Agent;
            foreach (var n in stepNeeds)
                yield return new Need(n, $"step {step.Id}", agent, agent ? step.Agent?.Role?.Reference?.Name : null);
        }

        foreach (var use in plan.Instances.Where(i => i.Tracker is not null))
            yield return new Need("tracker", $"fragment use {use.Id} (tracker)");
    }

    /// <summary>
    /// Check 21: every capability the plan needs is offered by the instance that serves it. <paramref name="offered"/> gives the
    /// names an instance offers, by type and the run's choice of instance (null for the type's own); an agent step's
    /// <c>agent_runtime</c> needs are asked of the instance its role maps to (<paramref name="runtimeOfRole"/>: the instance and
    /// the <c>role_mapping</c> key that chose it, the role's own or <c>default</c>; null where none is mapped), as the agent step
    /// picks it. A missing capability is reported once per instance, for the first that needs it, and a flag not at all where
    /// its type is already reported missing.
    /// </summary>
    public static void Capabilities(Plan plan, LibrarySet libraries, Func<string, string?, IReadOnlySet<string>> offered,
        Func<string?, (string Instance, string Key)?> runtimeOfRole, DiagnosticBag d)
    {
        var reported = new HashSet<(string, string?)>();
        foreach (var need in AllNeeds(plan, libraries))
        {
            var entry = need.AgentStep && need.Type == "agent_runtime" ? runtimeOfRole(need.Role) : null;
            var mapped = entry?.Instance;
            if (offered(need.Type, mapped).Contains(need.Name) || reported.Contains((need.Type, mapped)) || !reported.Add((need.Name, mapped)))
                continue;
            var provider = entry is { } e ? $"{need.Type} instance '{e.Instance}' (role_mapping.{e.Key})" : $"the configured {need.Type} provider";
            var what = need.Name.Contains('.') ? $"capability {need.Name} is not offered by {provider}"
                : mapped is null ? $"no {need.Name} provider is configured"
                : $"{provider} is not configured";
            d.Add(Checks.Capabilities, $"{what}; needed by {need.By}");
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
    /// Check 22 at run start: no step exceeds a locked organisation cap (docs/05 section 3.1). Covered here: each
    /// step's caps (<see cref="StepCaps"/>, the same function a patch is checked with); a decision asking for
    /// auto-resolution where the matching approval rule locks <c>auto</c> off.
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
        var caps = StepCaps.Of(policy);
        foreach (var step in plan.AllSteps)
        {
            foreach (var breach in caps.Breaches(step, allowance(step)))
                d.Add(Checks.LockedCap, breach, step.Location, step.Id);
            if (step.Decision is { Auto: { }, Type: { } type } && approval(type) is { Rule.Auto.Value: false } governing)
            {
                if (governing.Rule.Auto.Locked)
                    d.Add(Checks.LockedCap, $"{step.Id} asks for auto-resolution of {type}; {governing.Source} locks auto off", step.Location, step.Id);
                else
                    d.AddRange([new Diagnostic(Checks.LockedCap, Severity.Warning,
                        $"{step.Id} asks for auto-resolution of {type}; {governing.Source} does not allow it, so a person answers", step.Location, step.Id)]);
            }
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
