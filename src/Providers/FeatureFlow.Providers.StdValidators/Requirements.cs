namespace FeatureFlow.Providers.StdValidators;

/// <summary>The validators over plans alone: support, ownership, inheritance and verification methods (docs/01 section 8).</summary>
static class Requirements
{
    static string D(Read<PlanDocument> plan) => Check.Describe(plan.Subject);

    /// <summary>
    /// <c>requirement_support</c>. A link resolves to an item of another subject with the link's artifact name (a ticket's
    /// requirements link to its parent's, both named <c>requirements</c>). Coverage is of the first subject, by the other
    /// subjects of this run: std lists the upper document first (<c>[requirements, delivery_plan]</c>).
    /// </summary>
    public static async Task SupportAsync(Check c)
    {
        var plans = await c.PlansAsync(Types.Requirements, Types.DeliveryPlan);
        for (var p = 0; p < plans.Count; p++)
        {
            var plan = plans[p];
            // Another run's document is a target here; its own links are that run's to prove, and point at documents this
            // check is not given (an epic's requirements at its project's, LOG 199).
            if (plan.Subject.From != "current")
                continue;
            foreach (var item in plan.Value.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
            {
                var links = item.Supports ?? [];
                var reason = !string.IsNullOrWhiteSpace(item.SupportsReason);
                if (links.Count == 0 && !reason)
                    c.Error("unsupported", $"{item.Id} in {D(plan)} supports nothing and gives no supports_reason", plan.Subject.Name, item.Id);
                if (links.Count > 0 && reason)
                    c.Error("support_and_reason", $"{item.Id} in {D(plan)} both supports items and gives a supports_reason; it does one or the other", plan.Subject.Name, item.Id);
                foreach (var link in links)
                {
                    var targets = plans.Where((t, i) => i != p && Check.Names(t.Subject, link.Artifact)).ToList();
                    if (targets.Count == 0)
                        c.Error("unresolved_link",
                            c.Request.Subjects.Any(s => Check.Names(s, link.Artifact) && s.Absent)
                                ? $"{item.Id} in {D(plan)} supports {link.Requirement} in {link.Artifact}, which does not exist"
                                : $"{item.Id} in {D(plan)} supports {link.Requirement} in {link.Artifact}, which is not a subject of this check",
                            plan.Subject.Name, item.Id);
                    else if (!targets.Any(t => t.Value.Items.Any(x => x.Id == link.Requirement)))
                        c.Error("unresolved_link", $"{item.Id} in {D(plan)} supports {link.Requirement}, which {link.Artifact} does not hold", plan.Subject.Name, item.Id);
                }
            }
        }

        if (!c.Option("require_coverage", true) || c.Present(Types.Requirements, Types.DeliveryPlan) is not [var top, ..]
            || plans.FindIndex(r => r.Subject == top) is var at && at < 0)
            return;
        var supporters = plans.Where((r, i) => i != at && r.Subject.From == "current").ToList();
        if (supporters.Count == 0)
            return;
        foreach (var item in plans[at].Value.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
            if (!supporters.Any(s => s.Value.Items.Any(x => (x.Supports ?? []).Any(l => l.Artifact == top.Name && l.Requirement == item.Id))))
                c.Error("uncovered", $"no item of {string.Join(" or ", supporters.Select(D))} supports {item.Id} of {D(plans[at])}", top.Name, item.Id);
    }

    /// <summary><c>requirement_ownership</c>: every item of a parent-written plan names one owner its scope allows.</summary>
    public static async Task OwnershipAsync(Check c)
    {
        foreach (var plan in await c.PlansAsync(Types.Requirements, Types.DeliveryPlan))
        {
            var scope = plan.Value.Scope;
            if (Check.ChildKind(scope?.Kind) is not { } child)
            {
                c.Error("not_a_parent_plan", $"{D(plan)} is a {scope?.Kind ?? "(no scope)"} plan; only a project or an epic plan names owners", plan.Subject.Name);
                continue;
            }

            foreach (var item in plan.Value.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
                if (item.Owner is not { } owner)
                    c.Error("missing_owner", $"{item.Id} in {D(plan)} names no owner", plan.Subject.Name, item.Id);
                else if (string.IsNullOrEmpty(owner.Id) || !(owner.Kind == child || (owner.Kind == scope!.Kind && owner.Id == scope.Id)))
                    c.Error("invalid_owner",
                        $"{item.Id} in {D(plan)} is owned by {owner.Kind} {owner.Id}; a {scope!.Kind} plan's owner is a {child}, or the {scope.Kind} {scope.Id} itself",
                        plan.Subject.Name, item.Id);
        }
    }

    static bool Owns(PlanOwner? owner, PlanScope scope) => owner is not null && owner.Kind == scope.Kind && owner.Id == scope.Id;

    /// <summary>
    /// An item's links. A child's copy may name the parent's documents by the sandbox's names for them
    /// (<c>parent/requirements</c>, LOG 191); seen from <paramref name="from"/>'s run, that is <c>requirements</c>.
    /// </summary>
    static IEnumerable<(string?, string?)> Links(PlanItem item, FeatureFlow.Providers.Abstractions.ValidationSubject? from = null)
    {
        var prefix = from is null || from.From == "current" ? null : from.From.Replace(':', '/') + "/";
        return (item.Supports ?? [])
            .Select(l => (prefix is not null && l.Artifact?.StartsWith(prefix, StringComparison.Ordinal) == true ? l.Artifact[prefix.Length..] : l.Artifact, l.Requirement))
            .Distinct().Order();
    }

    /// <summary>
    /// <c>requirement_inheritance</c>: the parent's plan is every subject from another run, this run's plan every subject
    /// of its own. No parent plan passes (a standalone ticket, LOG 63).
    /// </summary>
    public static async Task InheritanceAsync(Check c)
    {
        var plans = await c.PlansAsync(Types.Requirements, Types.DeliveryPlan);
        var parents = plans.Where(p => p.Subject.From != "current").ToList();
        var own = plans.Where(p => p.Subject.From == "current").ToList();
        // A child's own plan never names owners: checked with or without a parent plan, so a standalone ticket cannot mark
        // its criteria as someone else's and so need no test (review F1).
        foreach (var plan in own)
            foreach (var item in plan.Value.Items.Where(i => !string.IsNullOrEmpty(i.Id) && i.Owner is not null))
                c.Error("owner_in_child", $"{item.Id} in {D(plan)} names an owner; only the parent's plan does", plan.Subject.Name, item.Id);
        if (parents.Count == 0)
            return;
        if (own.Count == 0)
        {
            c.Error("no_plan", "there is no plan of this run to check against the parent's");
            return;
        }

        var rewording = c.Option("allow_rewording", false);
        foreach (var plan in own)
        {
            if (plan.Value.Scope is not { Id: not null, Kind: not null } scope)
                continue;
            var owned = parents.SelectMany(p => p.Value.Items.Where(i => Owns(i.Owner, scope)).Select(i => (Parent: p, Item: i))).ToList();
            if (owned.Count == 0)
                c.Error("owns_nothing", $"the parent's plan gives {scope.Kind} {scope.Id} no item; a child that owns nothing cannot run", plan.Subject.Name);
            foreach (var (parent, item) in owned)
            {
                var copy = plan.Value.Items.FirstOrDefault(i => i.Id == item.Id);
                if (copy is null)
                    c.Error("missing_inherited", $"{D(plan)} does not hold {item.Id}, which {D(parent)} gives {scope.Kind} {scope.Id}", plan.Subject.Name, item.Id);
                else if (Links(item).Except(Links(copy, parent.Subject)).Any())
                    // A copy keeps every link the parent gave it; it may add links to this run's own items, which
                    // requirement_support resolves (LOG 191).
                    c.Error("links_changed", $"{item.Id} in {D(plan)} does not support everything it supports in {D(parent)}", plan.Subject.Name, item.Id);
                else if (!rewording && (copy.Title != item.Title || copy.Behaviour != item.Behaviour))
                    c.Error("reworded", $"{item.Id} in {D(plan)} is not word for word as in {D(parent)}", plan.Subject.Name, item.Id);
            }

            foreach (var item in plan.Value.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
            {
                if (parents.SelectMany(p => p.Value.Items).FirstOrDefault(i => i.Id == item.Id && i.Owner is not null && !Owns(i.Owner, scope)) is { Owner: { } other })
                    c.Error("owned_by_another", $"{D(plan)} holds {item.Id}, which the parent's plan gives {other.Kind} {other.Id}", plan.Subject.Name, item.Id);
            }
        }
    }

    /// <summary><c>verification_method_support</c>: a criterion is proved by its own methods, or by its owner's.</summary>
    public static async Task MethodSupportAsync(Check c)
    {
        foreach (var plan in await c.PlansAsync(Types.DeliveryPlan))
            foreach (var item in plan.Value.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
            {
                var methods = item.Verification ?? [];
                var delegated = Check.Delegated(item, plan.Value.Scope);
                if (!delegated && methods.Count == 0)
                    c.Error("no_method", item.Owner is null
                        ? $"{item.Id} in {D(plan)} has no owner and no verification method"
                        : $"{item.Id} in {D(plan)} has no verification method, and its owner {item.Owner.Kind} {item.Owner.Id} is not a child this {plan.Value.Scope?.Kind} plan may give it to",
                        plan.Subject.Name, item.Id);
                if (delegated && methods.Count > 0)
                    c.Error("owned_with_methods", $"{item.Id} in {D(plan)} is owned by {item.Owner!.Kind} {item.Owner.Id}, which adds its methods; it has its own", plan.Subject.Name, item.Id);
                foreach (var method in methods)
                {
                    if (string.IsNullOrEmpty(method.Id) || string.IsNullOrEmpty(method.Repository) || string.IsNullOrEmpty(method.Tier))
                        c.Error("malformed", $"a method of {item.Id} in {D(plan)} lacks an id, repository or tier", plan.Subject.Name, item.Id);
                    else if (c.Request.Repositories.Count > 0 && c.Frozen(method.Repository) is null)
                        c.Warn("unknown_repository", $"{item.Id}/{method.Id} names {method.Repository}, which this run has not prepared", plan.Subject.Name, item.Id);
                }

                foreach (var duplicate in methods.Where(m => !string.IsNullOrEmpty(m.Id)).GroupBy(m => m.Id).Where(g => g.Count() > 1))
                    c.Error("duplicate_id", $"{item.Id} in {D(plan)} has method {duplicate.Key} {duplicate.Count()} times", plan.Subject.Name, item.Id);
            }
    }
}
