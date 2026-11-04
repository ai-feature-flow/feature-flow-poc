namespace FeatureFlow.Model.Tests;

/// <summary>
/// The organisation file in full (docs/07 section 3): every key read into the model, unknown keys refused, caps as a
/// value or <c>{ value, locked }</c>, and the checks a reader can make alone, each failure naming the key and the reason.
/// </summary>
public class OrganisationTests
{
    /// <summary>docs/07 section 3's example, with a directory library (git arrives in M8) and a team's repositories (LOG 204).</summary>
    const string Full = """
        api_version: feature-flow/v1
        kind: organisation
        name: acme
        libraries:
          - { name: std, source: directory, path: ../library/std }
        library_overrides:
          std:tasks/implement_ticket: acme_delivery:tasks/implement_ticket
        library_inputs: { planning_repository: acme/plans }
        policy:
          approval:
            - { match: "*.merge_action", roles: [approver], min_count: 2, from_chat: false, auto: false }
            - { match: "*", roles: [approver], min_count: 1, from_chat: true, auto: true }
          merge:
            default_branch: { engine_may_merge: ["acme/poc-*"] }
            require_checks: { value: true, locked: true }
          attempts:
            ceiling: { per_step: 5, per_run: 40 }
            hard_ceiling: { per_step: 8, per_run: 60 }
          budgets:
            max_work_items: { ceiling: 30 }
          planning:
            allowed_roles: [planner, implementer, fixer]
            require_review: [{ match: "*:templates/project_*", value: true }]
          toolsets:
            allowed: [jira, confluence]
          overrides:
            skip_protected: [admin]
        roles:
          viewer:   { idp_groups: [acme-engineering] }
          approver: { idp_groups: [acme-leads] }
          steerer:  { idp_groups: [acme-leads] }
          admin:    { idp_groups: [acme-platform] }
        role_mapping:
          planner:     { agent_runtime: claude_code, model: claude-sonnet }
          default:     { agent_runtime: claude_code, model: claude-sonnet }
        notifications:
          routing:
            decision_needed: { channel: "#ff-decisions" }
            stuck:           { channel: "#ff-ops" }
          events: [decision_needed, failed, completed]
        timeouts:
          heartbeat: 30s
          stalled_after: 5m
        teams:
          payments:
            repositories: ["acme/payments-*"]
            policy:
              approval: [{ match: "*.merge_action", roles: [approver], min_count: 3 }]
            roles:
              approver: { idp_groups: [acme-payments-leads] }
        """;

    static (OrganisationConfig? Organisation, DiagnosticBag Diagnostics) Read(string text)
    {
        var d = new DiagnosticBag();
        return (ConfigurationReader.ReadOrganisation(text, "organisation.yaml", d), d);
    }

    [Fact]
    public void Docs_07s_example_reads_whole()
    {
        var (org, d) = Read(Full);

        Assert.Empty(d);
        Assert.Equal(["acme-leads"], org!.Roles["steerer"].IdpGroups);
        Assert.Equal(new AgentRoleMapping("claude_code", "claude-sonnet"), org.RoleMapping["planner"]);
        Assert.Equal("#ff-ops", org.Notifications.Routing["stuck"].Channel);
        Assert.Equal(["decision_needed", "failed", "completed"], org.Notifications.Events);
        Assert.Equal(new TimeoutsConfig(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)), org.Timeouts);
        Assert.Equal("acme_delivery:tasks/implement_ticket", org.LibraryOverrides["std:tasks/implement_ticket"]);
        Assert.Equal("acme/plans", org.LibraryInputs["planning_repository"]);
        Assert.Equal(["admin"], org.Policy.Overrides["skip_protected"]);
        Assert.Equal(["jira", "confluence"], org.Policy.AllowedToolsets);
        Assert.Equal(new ReviewRule("*:templates/project_*", new Cap<bool>(true, false)), org.Policy.RequireReview.Single());
        var team = org.Teams["payments"];
        Assert.Equal(["acme/payments-*"], team.Repositories);
        Assert.Equal(3L, team.Approval.Single().MinCount);
        Assert.Equal(["acme-payments-leads"], team.Roles["approver"].IdpGroups);
        Assert.Equal(new Cap<bool>(false, false), org.Policy.RuleFor("epic.merge_action")!.Auto);
        Assert.True(org.Policy.RuleFor("ticket.spec_approval")!.FromChat);
    }

    [Theory]
    // Unknown keys anywhere are refused, never ignored (docs/07 section 1).
    [InlineData("timeouts:\n  heartbeat: 30s", "timeouts:\n  heartbeat: 30s\n  retries: 3", "unknown key 'retries' in timeouts")]
    [InlineData("\nroles:\n", "\nrolez: {}\nroles:\n", "unknown key 'rolez'")]
    [InlineData("toolsets:\n    allowed:", "toolsets:\n    allow:", "unknown key 'allow'")]
    // People roles are the four of docs/05 section 3.3.
    [InlineData("  admin:    { idp_groups: [acme-platform] }", "  owner:    { idp_groups: [acme-platform] }", "roles.owner: not a people role")]
    [InlineData("  admin:    { idp_groups: [acme-platform] }", "  admin:    {}", "roles.admin must list its idp_groups")]
    [InlineData("roles: [approver], min_count: 1, from_chat: true", "roles: [approver, lead], min_count: 1, from_chat: true", "'lead' is not a people role")]
    [InlineData("roles: [approver], min_count: 1, from_chat: true", "roles: [], min_count: 1, from_chat: true", "must name at least one people role")]
    [InlineData("min_count: 2, from_chat: false", "min_count: 0, from_chat: false", "min_count must be at least 1")]
    // policy.overrides: only catalogue keys, and not answers, whose roles are approval's.
    [InlineData("skip_protected: [admin]", "skip_everything: [admin]", "policy.overrides.skip_everything: not a run-time override key")]
    [InlineData("skip_protected: [admin]", "answer: [steerer]", "who may answer is set by policy.approval")]
    [InlineData("skip_protected: [admin]", "skip_protected: [root]", "'root' is not a people role")]
    [InlineData("skip_protected: [admin]", "skip_protected: []", "must name at least one people role")]
    // A team tightens, never relaxes (docs/07 section 1).
    [InlineData("min_count: 3 }]", "min_count: 1 }]", "relaxes the organisation's rule '*.merge_action' (min_count 1 is below 2)")]
    [InlineData("min_count: 3 }]", "min_count: 3, from_chat: true }]", "(from_chat is allowed)")]
    [InlineData("min_count: 3 }]", "min_count: 3, auto: true }]", "(auto is allowed)")]
    [InlineData("    repositories: [\"acme/payments-*\"]\n", "", "teams.payments must list the repositories it owns")]
    [InlineData("      approval: [{", "      merge: { require_checks: false }\n      approval: [{", "a team's policy holds approval rules only")]
    // Malformed values.
    [InlineData("heartbeat: 30s", "heartbeat: soon", "heartbeat must be a duration")]
    [InlineData("std:tasks/implement_ticket: acme", "implement_ticket: acme", "both sides must be library references")]
    [InlineData("  planner:     { agent_runtime: claude_code, model: claude-sonnet }", "  planner:     { model: claude-sonnet }", "role_mapping.planner needs 'agent_runtime'")]
    [InlineData("require_review: [{ match: \"*:templates/project_*\", value: true }]", "require_review: [{ match: \"*:templates/project_*\" }]", "needs a value")]
    public void An_organisation_refuses_what_it_does_not_read(string old, string replacement, string message)
    {
        Assert.Contains(old, Full, StringComparison.Ordinal);
        var (_, d) = Read(Full.Replace(old, replacement, StringComparison.Ordinal));

        Assert.Contains(d, x => x.Blocks && x.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_team_may_lock_auto_off_where_the_organisation_allows_it()
    {
        var (_, d) = Read(Full.Replace("min_count: 3 }]", "min_count: 3, auto: { value: false, locked: true } }]", StringComparison.Ordinal)
            .Replace("\"*.merge_action\", roles: [approver], min_count: 3", "\"ticket.*\", roles: [approver], min_count: 3", StringComparison.Ordinal));
        Assert.Empty(d);
    }

    [Fact]
    public void Roles_nest_admin_over_steerer_over_viewer_and_approver_stands_apart()
    {
        Assert.Equal(["viewer", "steerer", "admin"], PeopleRoles.Expand(["admin"]));
        Assert.Equal(["viewer", "approver"], PeopleRoles.Expand(["approver"]));
        Assert.Equal(["viewer"], PeopleRoles.Expand(["viewer"]));
        Assert.DoesNotContain("approver", PeopleRoles.Expand(["admin"]));
    }

    [Fact]
    public void A_persons_roles_come_from_their_groups_and_a_team_role_is_held_only_through_the_teams_groups()
    {
        var org = Read(Full).Organisation!;
        var payments = org.Teams["payments"];

        Assert.Equal(["viewer", "approver", "steerer"], PeopleRoles.FromGroups(["acme-leads"], org, null));
        // In payments' scope, approver is held through acme-payments-leads only; steerer is still the organisation's.
        Assert.Equal(["viewer", "steerer"], PeopleRoles.FromGroups(["acme-leads"], org, payments));
        Assert.Equal(["viewer", "approver"], PeopleRoles.FromGroups(["acme-payments-leads"], org, payments));
        Assert.Empty(PeopleRoles.FromGroups(["acme-payments-leads"], org, null));
        Assert.Empty(PeopleRoles.FromGroups([], org, null));
    }

    [Fact]
    public void A_run_belongs_to_the_first_team_owning_one_of_its_repositories()
    {
        var org = Read(Full).Organisation!;
        Assert.Equal("payments", Approval.TeamFor(org, ["acme/web", "acme/payments-api"])?.Name);
        Assert.Null(Approval.TeamFor(org, ["acme/web"]));
    }

    static readonly SourceLocation Nowhere = new("", 0, 0);

    [Fact]
    public void Approval_is_the_teams_rule_then_the_organisations_then_the_librarys_default_then_the_engines()
    {
        var org = Read(Full).Organisation!;
        var team = org.Teams["payments"];
        DecisionTypeItem[] declared =
        [
            new("x.audit", null, Nowhere) { DefaultApproval = new ApprovalRule("x.audit", ["steerer"], 2, true, new Cap<bool>(true, false)) },
        ];
        var noRules = new PolicyConfig([], null, null, null, null);

        Assert.Equal((3L, "team payments approval rule '*.merge_action'"), Pick(Approval.For("epic.merge_action", org.Policy, team, declared)));
        Assert.Equal((2L, "approval rule '*.merge_action'"), Pick(Approval.For("epic.merge_action", org.Policy, null, declared)));
        Assert.Equal((2L, "library default_approval of x.audit"), Pick(Approval.For("x.audit", noRules, null, declared)));
        Assert.Equal((1L, "the engine's default"), Pick(Approval.For("y.other", noRules, null, declared)));
        Assert.Equal(["approver"], Approval.For("y.other", noRules, null, [], "question").Rule.Roles);
        Assert.Equal(["steerer"], Approval.For("y.escalation", noRules, null, [], "intervention").Rule.Roles);

        static (long, string) Pick(EffectiveApproval a) => (a.Rule.MinCount, a.Source);
    }

    [Fact]
    public void The_runs_auto_rules_read_in_the_same_order_so_the_first_match_is_the_governing_rule()
    {
        var policy = new PolicyConfig([new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, false))], null, null, null, null);
        var team = new TeamConfig("t", ["a/*"], [new ApprovalRule("ticket.spec_approval", ["approver"], 1, false, new Cap<bool>(true, false))], new Dictionary<string, PeopleRoleConfig>());
        DecisionTypeItem[] declared =
        [
            new("*.escalation", null, Nowhere) { DefaultApproval = new ApprovalRule("*.escalation", ["steerer"], 1, false, new Cap<bool>(false, false)) },
            new("epic.plan", null, Nowhere) { DefaultApproval = new ApprovalRule("epic.plan", ["approver"], 1, false, new Cap<bool>(true, false)) },
        ];

        static bool? First(IReadOnlyList<(string Match, bool Auto)> rules, string type) =>
            rules.Where(r => new DecisionTypeItem(r.Match, null, Nowhere).Matches(type) || r.Match == "*").Select(r => (bool?)r.Auto).FirstOrDefault();

        var withTeam = Approval.AutoRules(policy, team, declared);
        var without = Approval.AutoRules(policy, null, declared);
        Assert.True(First(withTeam, "ticket.spec_approval"));
        Assert.False(First(without, "ticket.spec_approval"));
        // No organisation rule matches: the library's default allows it.
        Assert.True(First(without, "epic.plan"));
        Assert.False(First(without, "epic.escalation"));
        foreach (var type in new[] { "ticket.spec_approval", "epic.plan", "epic.escalation" })
            Assert.Equal(Approval.For(type, policy, team, declared).Rule.Auto.Value, First(withTeam, type));
    }

    [Fact]
    public void A_librarys_default_approval_is_read_whole()
    {
        var d = new DiagnosticBag();
        var types = LibraryItemReader.ReadDecisionTypes(Yaml.YamlText.Parse("""
            api_version: feature-flow/v1
            kind: decision_types
            decision_types:
              - { name: a.b, default_approval: { roles: [approver], min_count: 2, from_chat: true, auto: true } }
              - { name: a.c }
            """, "t.yaml", d)!, d);

        Assert.Empty(d);
        var rule = types[0].DefaultApproval!;
        Assert.Equal(("a.b", 2L, true, new Cap<bool>(true, false)), (rule.Match, rule.MinCount, rule.FromChat, rule.Auto));
        Assert.Equal(["approver"], rule.Roles);
        Assert.Null(types[1].DefaultApproval);
    }

    [Theory]
    [InlineData("auth: { mode: oidc, issuer: \"https://idp.example\", audience: feature-flow, role_claim: grp, actor_claim: email }", null)]
    [InlineData("auth: { mode: oidc, issuer: \"http://127.0.0.1:9000\", audience: feature-flow }", null)]
    [InlineData("auth: { mode: oidc, issuer: \"http://idp.example\", audience: feature-flow }", "must be an https URL")]
    [InlineData("auth: { mode: oidc, audience: feature-flow }", "needs 'issuer'")]
    [InlineData("auth: { mode: oidc, issuer: \"https://idp.example\" }", "needs 'audience'")]
    [InlineData("auth: { mode: local_token, token_ref: secrets/t, issuer: \"https://idp.example\" }", "unknown key 'issuer'")]
    [InlineData("auth: { mode: local_token, token_ref: secrets/t, actor: policy }", "actor of automatic answers")]
    [InlineData("auth: { mode: local_token, token_ref: secrets/t, roles: [root] }", "'root' is not a people role")]
    public void The_installations_api_auth_reads_oidc_beside_the_local_token(string auth, string? error)
    {
        var d = new DiagnosticBag();
        var config = ConfigurationReader.ReadInstallation($"api_version: feature-flow/v1\nkind: installation\napi:\n  {auth}\n", "installation.yaml", d);
        if (error is not null)
        {
            Assert.Contains(d, x => x.Blocks && x.Message.Contains(error, StringComparison.Ordinal));
            return;
        }

        Assert.Empty(d);
        Assert.Equal("oidc", config!.ApiAuth.Mode);
        Assert.Equal("feature-flow", config.ApiAuth.Audience);
        if (auth.Contains("grp", StringComparison.Ordinal))
            Assert.Equal(("grp", "email"), (config.ApiAuth.GroupsClaim, config.ApiAuth.ActorClaim));
        else
            Assert.Equal(("groups", "sub"), (config.ApiAuth.GroupsClaim, config.ApiAuth.ActorClaim));
    }
}
