namespace FeatureFlow.Model.Tests;

/// <summary>
/// The organisation file in full (docs/07 section 3): every key read into the model, unknown keys refused, caps as a
/// value or <c>{ value, locked }</c>, and the checks a reader can make alone, each failure naming the key and the reason.
/// </summary>
public class OrganisationTests
{
    /// <summary>docs/07 section 3's example, with a directory library and a team's repositories (LOG 204).</summary>
    const string Full = """
        api_version: feature-flow/v1
        kind: organisation
        name: acme
        libraries:
          - { name: std, source: directory, path: ../library/std }
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
        teams:
          payments:
            repositories: ["acme/payments-*"]
            policy:
              approval: [{ match: "*.merge_action", min_count: 3 }]
            roles:
              approver: { idp_groups: [acme-payments-leads] }
        """;

    /// <summary>Keys version 1 leaves out (LOG 268 to 270), each as a minimal file sets it and as the refusal names it.</summary>
    static readonly (string Line, string Key)[] NotInVersion1 =
    [
        ("library_overrides: { \"std:tasks/a\": \"acme:tasks/a\" }", "library_overrides"),
        ("library_inputs: { planning_repository: acme/plans }", "library_inputs"),
        ("policy: { toolsets: { allowed: [jira] } }", "policy.toolsets"),
        ("timeouts: { heartbeat: 30s }", "timeouts"),
    ];

    static (OrganisationConfig? Organisation, DiagnosticBag Diagnostics) Read(string text)
    {
        var d = new DiagnosticBag();
        return (ConfigurationReader.ReadOrganisation(text, "organisation.yaml", d), d);
    }

    [Fact]
    public void Docs_07s_example_reads_whole()
    {
        var (org, d) = Read(Full);

        // Read and checked, but refused until M8's Agents work acts on it (LOG 256, 267).
        Assert.Equal(["role_mapping is not enforced before M8; remove it"], d.Select(x => x.Message));
        Assert.Equal(["acme-leads"], org!.Roles["steerer"].IdpGroups);
        Assert.Equal(new AgentRoleMapping("claude_code", "claude-sonnet"), org.RoleMapping["planner"]);
        Assert.Equal("#ff-ops", org.Notifications.Routing["stuck"].Channel);
        Assert.Equal(["decision_needed", "failed", "completed"], org.Notifications.Events);
        Assert.Equal(["admin"], org.Policy.Overrides["skip_protected"]);
        Assert.Equal(new ReviewRule("*:templates/project_*", new Cap<bool>(true, false)), org.Policy.RequireReview.Single());
        var team = org.Teams["payments"];
        Assert.Equal(["acme/payments-*"], team.Repositories);
        Assert.Equal(3L, team.Approval.Single().MinCount);
        Assert.Equal(["acme-payments-leads"], team.Roles["approver"].IdpGroups);
        Assert.Equal(new Cap<bool>(false, false), org.Policy.RuleFor("epic.merge_action")!.Auto);
        Assert.True(org.Policy.RuleFor("ticket.spec_approval")!.FromChat);
    }

    [Fact]
    public void A_git_library_source_reads_its_repository_and_ref()
    {
        var (org, d) = Read("""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries:
              - { name: acme_delivery, source: git, repository: "https://git.example/acme/library.git", ref: v3 }
              - { name: acme_tools, source: git, repository: "git@git.example:acme/tools.git" }
            """);

        Assert.Empty(d);
        Assert.Equal(new LibrarySourceConfig("acme_delivery", "git", "", "https://git.example/acme/library.git", "v3"), org!.Libraries[0]);
        Assert.Equal(new LibrarySourceConfig("acme_tools", "git", "", "git@git.example:acme/tools.git", null), org.Libraries[1]);
    }

    [Theory]
    [InlineData("{ name: a, source: git, ref: main }", "needs 'repository'")]
    [InlineData("{ name: a, source: git, repository: r, path: lib }", "unknown key 'path'")]
    [InlineData("{ name: a, source: git, repository: r, ref: --upload-pack=x }", "libraries.a.ref must be a branch or tag name")]
    [InlineData("{ name: a, source: directory, path: lib, repository: r }", "unknown key 'repository'")]
    [InlineData("{ name: a, source: package, path: lib }", "the package library source is not supported in version 1")]
    public void A_library_source_takes_only_its_own_keys(string library, string message)
    {
        var (_, d) = Read($"api_version: feature-flow/v1\nkind: organisation\nname: acme\nlibraries:\n  - {library}\n");

        Assert.Contains(d, x => x.Blocks && x.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    // Unknown keys anywhere are refused, never ignored (docs/07 section 1).
    [InlineData("notifications:\n", "notifications:\n  retries: 3\n", "unknown key 'retries' in notifications")]
    [InlineData("\nroles:\n", "\nrolez: {}\nroles:\n", "unknown key 'rolez'")]
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
    // A team's rule sets only min_count (LOG 217): roles, chat and auto are the organisation's.
    [InlineData("min_count: 3 }]", "min_count: 3, roles: [viewer] }]", "a team's rule sets only match and min_count; roles is the organisation's")]
    [InlineData("min_count: 3 }]", "min_count: 3, from_chat: true }]", "from_chat is the organisation's")]
    [InlineData("min_count: 3 }]", "min_count: 3, auto: true }]", "auto is the organisation's")]
    [InlineData("min_count: 3 }]", "min_count: 0 }]", "needs a min_count of at least 1")]
    [InlineData("    repositories: [\"acme/payments-*\"]\n", "", "teams.payments must list the repositories it owns")]
    [InlineData("      approval: [{", "      merge: { require_checks: false }\n      approval: [{", "a team's policy holds approval rules only")]
    // Malformed values.
    [InlineData("  planner:     { agent_runtime: claude_code, model: claude-sonnet }", "  planner:     { model: claude-sonnet }", "role_mapping.planner needs 'agent_runtime'")]
    [InlineData("require_review: [{ match: \"*:templates/project_*\", value: true }]", "require_review: [{ match: \"*:templates/project_*\" }]", "needs a value")]
    public void An_organisation_refuses_what_it_does_not_read(string old, string replacement, string message)
    {
        Assert.Contains(old, Full, StringComparison.Ordinal);
        var (_, d) = Read(Full.Replace(old, replacement, StringComparison.Ordinal));

        Assert.Contains(d, x => x.Blocks && x.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Each_key_not_in_version_1_is_refused_alone_whatever_it_holds()
    {
        var minimal = "api_version: feature-flow/v1\nkind: organisation\nname: a\nlibraries: []\n";
        foreach (var (line, key) in NotInVersion1)
        {
            var (_, d) = Read(minimal + line + "\n");
            Assert.Equal($"{key} is not supported in version 1; remove it", Assert.Single(d, x => x.Blocks).Message);
        }

        // Its content is not read: a malformed value is refused for the key alone.
        Assert.Equal("timeouts is not supported in version 1; remove it", Assert.Single(Read(minimal + "timeouts: { heartbeat: soon }\n").Diagnostics).Message);
        Assert.Equal("policy.toolsets is not supported in version 1; remove it", Assert.Single(Read(minimal + "policy: { toolsets: {} }\n").Diagnostics).Message);
        Assert.Equal("role_mapping is not enforced before M8; remove it",
            Assert.Single(Read(minimal + "role_mapping: { default: { agent_runtime: claude_code } }\n").Diagnostics, x => x.Blocks).Message);
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
    public void A_persons_roles_come_from_their_groups_and_a_team_adds_holders_in_its_own_scope_only()
    {
        var org = Read(Full).Organisation!;
        var payments = org.Teams["payments"];

        Assert.Equal(["viewer", "approver", "steerer"], PeopleRoles.FromGroups(["acme-leads"], org, null));
        // In payments' scope, acme-payments-leads also hold approver; the organisation's holders keep it (LOG 217).
        Assert.Equal(["viewer", "approver", "steerer"], PeopleRoles.FromGroups(["acme-leads"], org, payments));
        Assert.Equal(["viewer", "approver"], PeopleRoles.FromGroups(["acme-payments-leads"], org, payments));
        Assert.Empty(PeopleRoles.FromGroups(["acme-payments-leads"], org, null));
        Assert.Empty(PeopleRoles.FromGroups([], org, null));
    }

    [Fact]
    public void A_run_may_be_in_one_teams_scope_at_most()
    {
        var org = Read(Full.Replace("teams:\n", "teams:\n  misc:\n    repositories: [\"acme/misc\", \"acme/payments-old\"]\n", StringComparison.Ordinal)).Organisation!;
        Assert.Equal("payments", Approval.TeamFor(org, ["acme/web", "acme/payments-api"])?.Name);
        Assert.Null(Approval.TeamFor(org, ["acme/web"]));
        Assert.Null(Approval.MoreThanOneTeam(org, ["acme/web", "acme/payments-api"]));
        // Adding a repository of another team, or a repository two teams match, would let the run pick the looser team.
        Assert.Contains("more than one team (misc, payments)", Approval.MoreThanOneTeam(org, ["acme/payments-api", "acme/misc"]), StringComparison.Ordinal);
        Assert.Contains("more than one team", Approval.MoreThanOneTeam(org, ["acme/payments-old"]), StringComparison.Ordinal);
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

        Assert.Equal((3L, "approval rule '*.merge_action', min_count from team payments's rule '*.merge_action'"), Pick(Approval.For("epic.merge_action", org.Policy, team, declared)));
        Assert.Equal(["approver"], Approval.For("epic.merge_action", org.Policy, team, declared).Rule.Roles);
        // A team's rule never lowers min_count, whatever its pattern.
        var lower = team with { Approval = [new ApprovalRule("*", [], 1, false, new Cap<bool>(false, false))] };
        Assert.Equal((2L, "approval rule '*.merge_action'"), Pick(Approval.For("epic.merge_action", org.Policy, lower, declared)));
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
        DecisionTypeItem[] declared =
        [
            new("*.escalation", null, Nowhere) { DefaultApproval = new ApprovalRule("*.escalation", ["steerer"], 1, false, new Cap<bool>(false, false)) },
            new("epic.plan", null, Nowhere) { DefaultApproval = new ApprovalRule("epic.plan", ["approver"], 1, false, new Cap<bool>(true, false)) },
        ];

        static bool? First(IReadOnlyList<(string Match, bool Auto)> rules, string type) =>
            rules.Where(r => new DecisionTypeItem(r.Match, null, Nowhere).Matches(type) || r.Match == "*").Select(r => (bool?)r.Auto).FirstOrDefault();

        var without = Approval.AutoRules(policy, declared);
        Assert.False(First(without, "ticket.spec_approval"));
        // No organisation rule matches: the library's default allows it.
        Assert.True(First(without, "epic.plan"));
        Assert.False(First(without, "epic.escalation"));
        foreach (var type in new[] { "ticket.spec_approval", "epic.plan", "epic.escalation" })
            Assert.Equal(Approval.For(type, policy, null, declared).Rule.Auto.Value, First(without, type));
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
