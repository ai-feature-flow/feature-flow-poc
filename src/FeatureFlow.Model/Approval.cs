namespace FeatureFlow.Model;

/// <summary>
/// People roles (docs/05 section 3.3) and how they nest: an administrator holds what a steerer holds, and a steerer and an
/// approver what a viewer holds. <c>approver</c> stands apart from <c>steerer</c> and <c>admin</c>: answering a decision
/// is identity governance, so steering a run does not by itself let a person approve its gates (LOG 216).
/// </summary>
public static class PeopleRoles
{
    public const string Viewer = "viewer";
    public const string Approver = "approver";
    public const string Steerer = "steerer";
    public const string Admin = "admin";

    /// <summary>The actor of an automatic answer (docs/04 section 4.2). No authenticated person may have it (F8).</summary>
    public const string PolicyActor = "policy";

    /// <summary>The roles held, with every role they imply.</summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> held)
    {
        var set = new HashSet<string>(held, StringComparer.Ordinal);
        if (set.Contains(Admin))
            set.Add(Steerer);
        if (set.Contains(Steerer) || set.Contains(Approver))
            set.Add(Viewer);
        return ConfigurationReader.PeopleRoleNames.Where(set.Contains).Concat(set.Where(r => !ConfigurationReader.PeopleRoleNames.Contains(r)).Order(StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// A person's roles from their identity-provider groups (docs/05 section 3.3): the organisation's <c>roles</c>, and in the
    /// run's team's scope also the team's. A team adds holders for its own runs; it never takes a role away (LOG 217).
    /// </summary>
    public static IReadOnlyList<string> FromGroups(IReadOnlyCollection<string> groups, OrganisationConfig organisation, TeamConfig? team)
    {
        var held = new List<string>();
        foreach (var role in ConfigurationReader.PeopleRoleNames)
        {
            var byOrganisation = organisation.Roles.GetValueOrDefault(role)?.IdpGroups ?? [];
            var byTeam = team?.Roles.GetValueOrDefault(role)?.IdpGroups ?? [];
            if (byOrganisation.Concat(byTeam).Any(groups.Contains))
                held.Add(role);
        }

        return Expand(held);
    }
}

/// <summary>The approval rule that governs a decision type, and the setting it came from, for the record and the effective policy view.</summary>
public sealed record EffectiveApproval(ApprovalRule Rule, string Source);

/// <summary>
/// Who may answer a decision (docs/05 sections 3.2 and 4), in one place for the API, run start and the effective policy
/// view: the first matching rule of the organisation, else the <c>default_approval</c> of the library that declares the type
/// (an exact name before a pattern), else the engine's default. A team's matching rule can only raise its <c>min_count</c>
/// (LOG 217), so a team never loosens who may answer.
/// </summary>
public static class Approval
{
    /// <summary>Every team whose repositories include one of the run's. Run start refuses a run with more than one (LOG 217).</summary>
    public static IReadOnlyList<TeamConfig> TeamsFor(OrganisationConfig organisation, IReadOnlyList<string> repositories) =>
        organisation.Teams.Values.Where(t => repositories.Any(r => RepositoryPatterns.Any(t.Repositories, r))).ToList();

    /// <summary>
    /// Why a run may not start, or null: its repositories (or one repository) belong to more than one team, so it could pick
    /// a looser team by adding a repository (LOG 217).
    /// </summary>
    public static string? MoreThanOneTeam(OrganisationConfig organisation, IReadOnlyList<string> repositories) =>
        TeamsFor(organisation, repositories) is { Count: > 1 } teams
            ? $"the run's repositories ({string.Join(", ", repositories)}) belong to more than one team ({string.Join(", ", teams.Select(t => t.Name))}); start one run per team"
            : null;

    /// <summary>The run's team, or null for none. A run in more than one team's scope never starts, so the first is the only one.</summary>
    public static TeamConfig? TeamFor(OrganisationConfig organisation, IReadOnlyList<string> repositories) => TeamsFor(organisation, repositories).FirstOrDefault();

    /// <summary>
    /// The rule for <paramref name="type"/>. <paramref name="kind"/> is the wait's kind, which picks the engine's default
    /// where nothing matches: <c>approver</c> for a decision or an agent's question, <c>steerer</c> for the rest.
    /// </summary>
    public static EffectiveApproval For(string type, PolicyConfig policy, TeamConfig? team, IReadOnlyList<DecisionTypeItem> declared, string kind = "decision")
    {
        var governing = Governing(type, policy, declared, kind);
        if (team?.Approval.FirstOrDefault(r => r.Matches(type)) is { } teamRule && teamRule.MinCount > governing.Rule.MinCount)
            return new(governing.Rule with { MinCount = teamRule.MinCount }, $"{governing.Source}, min_count from team {team.Name}'s rule '{teamRule.Match}'");
        return governing;
    }

    static EffectiveApproval Governing(string type, PolicyConfig policy, IReadOnlyList<DecisionTypeItem> declared, string kind)
    {
        if (policy.RuleFor(type) is { } rule)
            return new(rule, $"approval rule '{rule.Match}'");
        if (Declared(type, declared) is { } item)
            return new(item.DefaultApproval ?? new ApprovalRule(item.Name, item.DefaultRoles, 1, false, new Cap<bool>(false, false)),
                $"library default_approval of {item.Name}");
        return new(new ApprovalRule("*", [kind is "decision" or "question" ? PeopleRoles.Approver : PeopleRoles.Steerer], 1, false, new Cap<bool>(false, false)),
            "the engine's default");
    }

    /// <summary>
    /// What the run carries to decide auto-resolution in workflow code (docs/05 section 8), in the order <see cref="For"/>
    /// reads them, so the first match in the list is the rule <see cref="For"/> finds. A team's rules do not take part.
    /// </summary>
    public static IReadOnlyList<(string Match, bool Auto)> AutoRules(PolicyConfig policy, IReadOnlyList<DecisionTypeItem> declared)
    {
        var list = new List<(string, bool)>();
        list.AddRange(policy.Approval.Select(r => (r.Match, r.Auto.Value)));
        foreach (var item in declared.Where(t => !t.Name.Contains('*')).Concat(declared.Where(t => t.Name.Contains('*'))))
            list.Add((item.Name, item.DefaultApproval?.Auto.Value ?? false));
        return list;
    }

    static DecisionTypeItem? Declared(string type, IReadOnlyList<DecisionTypeItem> declared) =>
        declared.FirstOrDefault(t => t.Name == type) ?? declared.FirstOrDefault(t => t.Matches(type));
}
