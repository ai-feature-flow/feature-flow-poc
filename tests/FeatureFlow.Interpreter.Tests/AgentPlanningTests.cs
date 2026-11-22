using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// An agent's planning authority in the core (design note, agent planning, s1 to s6): the plan an agent may see, the preview of
/// its patch, which must be what the patch path then does, <c>planning.templates</c>, a child's <c>review</c> held at the parent,
/// and the approval prompt.
/// </summary>
public class AgentPlanningTests
{
    static string Planned(string name, string planning) => $$"""
        api_version: feature-flow/v1
        kind: template
        metadata: { name: {{name}}, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              {{planning}}
            outcomes: { planned: success, blocked: blocked }
          - id: build
            action: test.run
          - id: review
            action: artifact.validate
            protected: true
          - id: ship
            action: artifact.compose
            routes: { composed: { end: completed } }
        """;

    const string Kid = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: kid, level: ticket }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [parent], review: required }
            outcomes: { planned: success, blocked: blocked }
            routes: { planned: { end: completed }, blocked: { escalate: true } }
        """;

    const string Parent = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: parent, level: epic }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: kid
            action: run.child
            with: { template: probe:templates/kid, repositories: [acme/api], parameters: {} }
          - id: ship
            action: artifact.compose
            routes: { composed: { end: completed } }
        """;

    static readonly Probe Lib = new("agent-planning",
        ("auto", Planned("auto", "planning: { targets: [self] }")),
        ("required", Planned("required", "planning: { targets: [self], review: required }")),
        ("granted", Planned("granted", "planning: { targets: [self], templates: [probe:templates/kid] }")),
        ("parent_only", Planned("parent_only", "planning: { targets: [parent] }")),
        ("none", Planned("none", "skills: []")),
        ("kid", Kid), ("parent", Parent),
        ("fenced", """
            api_version: feature-flow/v1
            kind: template
            metadata: { name: fenced, level: ticket }
            defaults:
              routes:
                by_class: { failure: { escalate: true }, blocked: { escalate: true } }
            steps:
              - id: plan
                agent:
                  role: std:roles/planner
                  task: std:tasks/repair_failures
                  planning: { targets: [self] }
                outcomes: { planned: success, blocked: blocked }
                routes: { planned: base }
              - id: base
                use: std:fragments/baseline
                protected: true
                routes: { done: lanes }
              - id: lanes
                parallel:
                  branches:
                    - { id: a, action: test.run }
                protected: true
                routes: { completed: ship, any_failed: { escalate: true } }
              - id: ship
                action: artifact.compose
                with: { name: release, members: [team] }
                routes: { composed: { end: completed } }
            """));

    static RunInfo Run(string? parent = null) => new("run-1", ["acme/api"], new Dictionary<string, YNode>(), parent);

    static RunCore Started(string template, RunOptions? options = null, string? parent = null)
    {
        var (core, d) = RunCore.Start(Lib.Libraries, Probe.Ref(template), Run(parent), options);
        Assert.True(core is not null, string.Join("\n", d));
        foreach (var start in core!.Advance().OfType<StartChild>())
            core.Apply(new ChildStarted(start.StepId, start.Execution, $"run-1.{start.StepId}.{start.Execution}"));
        return core;
    }

    static string Doc(string changes, long revision = 1, string? resumeAt = "build", string reason = "test") => $"""
        api_version: feature-flow/v1
        kind: patch
        metadata: {JsonSerializer.Serialize(new Dictionary<string, object> { ["proposed_by"] = new { type = "agent", step_id = "plan" }, ["reason"] = reason })}
        base_revision: {revision}
        {(resumeAt is null ? "" : $"resume_at: {resumeAt}")}
        changes:
        {changes}
        """;

    static PatchDocument Read(string yaml)
    {
        var d = new DiagnosticBag();
        var patch = PatchReader.Read(YamlText.Parse(yaml, "patch.yaml", d)!, d);
        Assert.True(!d.HasErrors && patch is not null, string.Join("\n", d));
        return patch!;
    }

    const string Key = "run-1/plan/1";
    const string InsertLint = "  - { op: insert, after: build, step: { id: lint, action: analysis.run } }";
    const string TouchReview = "  - { op: set, target: review, field: limits, value: { attempts: 1 } }";
    const string ChangeRole = "  - { op: replace, target: plan, step: { id: plan_again, agent: { role: std:roles/implementer, task: std:tasks/repair_failures }, outcomes: { done: success } } }";
    const string AboveCap = "  - { op: set, target: build, field: limits, value: { attempts: 50 } }";
    const string InsertChild = "  - { op: insert, after: build, step: { id: more, action: run.child, with: { template: probe:templates/kid, repositories: [acme/api], parameters: {} } } }";

    public static TheoryData<string, string, string, long> Cases => new()
    {
        { "auto", InsertLint, PatchPreview.Applies, 1 },
        { "required", InsertLint, PatchPreview.NeedsApproval, 1 },
        { "auto", TouchReview, PatchPreview.NeedsApproval, 1 },
        { "auto", ChangeRole, PatchPreview.Refused, 1 },
        { "auto", AboveCap, PatchPreview.Refused, 1 },
        { "auto", InsertLint, PatchPreview.Refused, 2 },
        { "none", InsertLint, PatchPreview.Refused, 1 },
        { "parent_only", InsertLint, PatchPreview.Refused, 1 },
        { "auto", InsertChild, PatchPreview.NeedsApproval, 1 },
        { "granted", InsertChild, PatchPreview.Applies, 1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_preview_is_what_the_patch_path_then_does_with_the_same_patch(string template, string changes, string expected, long revision)
    {
        var core = Started(template, new RunOptions { StepCeiling = 5 });
        var yaml = Doc(changes, revision);

        var preview = core.PreviewPatch(new PatchPreviewQuery(Key, yaml, ToParent: false));
        var result = core.Apply(new StepCompleted("plan", "planned", Patch: Read(yaml)));

        Assert.Equal(expected, preview.Status);
        Assert.Equal(expected switch
        {
            PatchPreview.Applies => PatchStatus.Applied,
            PatchPreview.NeedsApproval => PatchStatus.Pending,
            _ => PatchStatus.Rejected,
        }, result.Patch!.Status);
        if (expected == PatchPreview.Refused)
            Assert.Equal(result.Errors.Select(e => e["plan: ".Length..]), preview.Messages);
        if (expected == PatchPreview.NeedsApproval)
            Assert.Equal(core.State.Waits.Single(w => w.Kind == WaitKind.PatchApproval).Reason, string.Join("; ", preview.Reasons));
    }

    [Fact]
    public void The_refusals_name_the_rule_for_a_step_without_planning_a_role_change_and_a_parent_only_grant()
    {
        Assert.Contains("no planning authority over this run's plan", Started("none").PreviewPatch(new(Key, Doc(InsertLint), false)).Messages.Single());
        Assert.Contains("no planning authority over this run's plan", Started("parent_only").PreviewPatch(new(Key, Doc(InsertLint), false)).Messages.Single());
        Assert.Contains("an agent never changes a role (P8)", Started("auto").PreviewPatch(new(Key, Doc(ChangeRole), false)).Messages.Single());
        Assert.Contains("has planning.review: required", Started("required").PreviewPatch(new(Key, Doc(InsertLint), false)).Reasons.Single());
        Assert.Contains("touches protected step review (F3)", Started("auto").PreviewPatch(new(Key, Doc(TouchReview), false)).Reasons.Single());
        Assert.Equal(PatchPreview.Unavailable, Started("auto").PreviewPatch(new("run-1/plan/2", Doc(InsertLint), false)).Status);
    }

    [Fact]
    public void A_preview_changes_no_state_and_sends_nothing()
    {
        var core = Started("auto");
        core.Flush();
        var before = JsonSerializer.Serialize(core.Snapshot());

        foreach (var changes in new[] { InsertLint, TouchReview, ChangeRole, InsertChild })
            core.PreviewPatch(new PatchPreviewQuery(Key, Doc(changes), ToParent: false));
        core.AgentPlan(new AgentPlanQuery(Key, "self"));

        Assert.Equal(before, JsonSerializer.Serialize(core.Snapshot()));
        Assert.Empty(core.Flush());
        Assert.Equal(1, core.State.Revision);
    }

    [Fact]
    public void To_the_parent_a_preview_forwards_only_when_the_step_may_and_there_is_a_parent()
    {
        var forwarded = Started("parent_only", parent: "epic-1").PreviewPatch(new(Key, Doc(InsertLint), ToParent: true));
        var orphan = Started("parent_only").PreviewPatch(new(Key, Doc(InsertLint), ToParent: true));
        var selfOnly = Started("auto", parent: "epic-1").PreviewPatch(new(Key, Doc(InsertLint), ToParent: true));

        Assert.Equal((PatchPreview.Forward, "epic-1"), (forwarded.Status, forwarded.ForwardTo));
        Assert.Contains("no parent", orphan.Messages.Single());
        Assert.Contains("planning.targets", selfOnly.Messages.Single());
    }

    [Fact]
    public void The_agents_plan_shows_its_step_authority_and_steps_but_not_what_a_wait_holds()
    {
        var core = Started("required", new RunOptions { AllowedRoles = ["planner"], RequireReview = true });
        Assert.True(core.Apply(new QuestionOpened("q1", "plan", Key, "SECRET question for a person", ["yes"], TimeSpan.FromHours(1), DateTimeOffset.UnixEpoch)).Accepted);

        var view = core.AgentPlan(new AgentPlanQuery(Key, "self"));

        Assert.Equal((AgentPlanView.Ok, "run-1", 1, "ticket"), (view.Status, view.RunId, view.Revision, view.Level));
        Assert.Equal(new AgentPlanSelf("plan", 1), view.You);
        Assert.Equal(["self"], view.Authority!.Targets);
        Assert.Equal(["planner"], view.Authority.AllowedRoles!);
        Assert.Equal(("required", true), (view.Authority.Review, view.Authority.RequireReview));
        Assert.Equal(["plan", "build", "review", "ship"], view.Steps.Select(s => s.Id));
        var plan = view.Steps[0];
        Assert.Equal(("agent", "planner", "waiting(question)"), (plan.Form, plan.What, plan.Status));
        Assert.Contains("role: std:roles/planner", plan.Source);
        Assert.True(view.Steps.Single(s => s.Id == "review").Protected);
        Assert.Contains("plan", view.Positions);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public void The_agents_plan_forwards_to_the_parent_and_a_parent_shows_its_plan_only_to_a_running_child()
    {
        var child = Started("parent_only", parent: "epic-1");
        var parent = Started("parent");
        var childId = parent.State.Children.Single().WorkflowId;

        var forwarded = child.AgentPlan(new AgentPlanQuery(Key, "parent"));
        var shown = parent.AgentPlan(new AgentPlanQuery(childId, "child"));
        var stranger = parent.AgentPlan(new AgentPlanQuery("someone-else", "child"));

        Assert.Equal((AgentPlanView.Forward, "epic-1"), (forwarded.Status, forwarded.ForwardTo));
        Assert.Equal((AgentPlanView.Ok, null), (shown.Status, shown.You));
        Assert.Equal(["kid", "ship"], shown.Steps.Select(s => s.Id));
        Assert.Equal(AgentPlanView.Unavailable, stranger.Status);
        Assert.Equal(AgentPlanView.Unavailable, Started("auto").AgentPlan(new AgentPlanQuery(Key, "parent")).Status);
    }

    [Fact]
    public void A_run_child_outside_planning_templates_waits_for_a_person_and_one_inside_applies()
    {
        var (_, unlisted) = Proposed("auto", InsertChild);
        var (_, listed) = Proposed("granted", InsertChild);
        var (_, other) = Proposed("granted", InsertChild.Replace("probe:templates/kid", "probe:templates/parent", StringComparison.Ordinal));

        Assert.Equal(PatchStatus.Pending, unlisted.Status);
        Assert.Contains("more starts template probe:templates/kid, which is not in planning.templates", unlisted.Diagnostics.Single().Message);
        Assert.Equal(PatchStatus.Applied, listed.Status);
        Assert.Equal(PatchStatus.Pending, other.Status);
    }

    static (RunCore Core, PatchResult Result) Proposed(string template, string changes)
    {
        var core = Started(template);
        return (core, core.Apply(new StepCompleted("plan", "planned", Patch: Read(Doc(changes)))).Patch!);
    }

    const string AddShip = "  - { op: insert, after: kid, step: { id: notice, action: artifact.compose } }";

    /// <summary>
    /// F3 covers what is inside protected content: a step inserted before a step of a protected fragment, or added as a branch
    /// of a protected parallel, waits for a person; one inserted before a protected step from outside it does not.
    /// </summary>
    [Theory]
    [InlineData("  - { op: insert, before: base.analysis, step: { id: lint, action: analysis.run, routes: { passed: analysis, failed: { escalate: true } } } }", "adds base.lint inside protected content (F3)")]
    [InlineData("  - { op: add_branch, target: lanes, step: { id: b, action: test.run } }", "adds b inside protected content (F3)")]
    [InlineData("  - { op: insert, before: ship, step: { id: lint, action: analysis.run, routes: { passed: ship, failed: { escalate: true } } } }", null)]
    public void A_step_added_inside_protected_content_waits_for_a_person(string changes, string? reason)
    {
        var core = Started("fenced");

        var result = core.Apply(new StepCompleted("plan", "planned", Patch: Read(Doc(changes, resumeAt: "base")))).Patch!;

        Assert.True(result.Status != PatchStatus.Rejected, string.Join("; ", result.Diagnostics));
        if (reason is null)
            Assert.Equal(PatchStatus.Applied, result.Status);
        else
            Assert.Contains(reason, result.Diagnostics.Select(d => d.Message));
    }

    static string Helper(string planning, string op = "insert, after: build", string id = "helper") =>
        $"  - {{ op: {op}, step: {{ id: {id}, agent: {{ role: std:roles/planner, task: std:tasks/repair_failures, planning: {planning} }}, outcomes: {{ planned: success, blocked: blocked }} }} }}";

    /// <summary>
    /// An agent does not widen planning authority without a person: a step the patch adds or replaces with targets, templates or
    /// review beyond the proposer's grant waits, with the reason; the same grant applies; a child's patch adding planning is held.
    /// </summary>
    [Theory]
    [InlineData("auto", "{ targets: [self, parent] }", "insert, after: build", "helper gets planning.targets parent, beyond the proposer's")]
    [InlineData("granted", "{ targets: [self], templates: [probe:templates/kid, probe:templates/parent] }", "insert, after: build",
        "helper gets planning.templates probe:templates/parent, outside the proposer's")]
    [InlineData("required", "{ targets: [self], review: auto }", "insert, after: build", "helper gets planning.review: auto, where the proposer's is required")]
    [InlineData("auto", "{ targets: [self, parent] }", "replace, target: plan", "plan_again gets planning.targets parent, beyond the proposer's")]
    [InlineData("auto", "{ targets: [self] }", "insert, after: build", null)]
    public void A_step_given_wider_planning_than_the_proposers_waits_for_a_person(string template, string planning, string op, string? reason)
    {
        var id = op.StartsWith("replace", StringComparison.Ordinal) ? "plan_again" : "helper";
        var (_, result) = Proposed(template, Helper(planning, op, id));

        Assert.True(result.Status != PatchStatus.Rejected, string.Join("; ", result.Diagnostics));
        if (reason is null)
            Assert.Equal(PatchStatus.Applied, result.Status);
        else
            Assert.Contains(reason, result.Diagnostics.Select(d => d.Message));
    }

    [Fact]
    public void A_childs_patch_that_adds_planning_is_held_at_the_parent()
    {
        var parent = Started("parent");
        var patch = Read(Doc(Helper("{ targets: [self] }", "insert, after: kid"), resumeAt: null));

        var result = parent.Apply(new ChildPatchProposed("kid", patch, "plan")).Patch!;

        Assert.Equal(PatchStatus.Pending, result.Status);
        Assert.Contains("helper gets planning authority; the parent does not hold the child step's grant to compare it with", result.Diagnostics.Select(d => d.Message));
    }

    [Fact]
    public void Inspecting_its_own_plan_needs_self_in_the_steps_targets()
    {
        var view = Started("parent_only").AgentPlan(new AgentPlanQuery(Key, "self"));

        Assert.Equal(AgentPlanView.Unavailable, view.Status);
        Assert.Equal("plan has no planning authority over this run's plan (docs/04 section 6.1)", view.Message);
    }

    [Fact]
    public void A_childs_review_required_is_carried_to_the_parent_whose_approval_opens_at_the_run_child_step()
    {
        var child = Started("kid", parent: "epic-1");
        Assert.True(child.Apply(new StepCompleted("plan", "planned", Patch: Read(Doc(AddShip, resumeAt: null)), PatchToParent: true)).Accepted);
        var sent = Assert.Single(child.Advance().OfType<SendPatchToParent>());
        Assert.True(sent.ReviewRequired);

        var parent = Started("parent");
        var childId = parent.State.Children.Single().WorkflowId;
        var yaml = PatchReader.Write(sent.Patch);
        var preview = parent.PreviewChildPatch(new ChildPatchPreviewQuery(childId, "plan", yaml, sent.ReviewRequired));
        var result = parent.Apply(new ChildPatchProposed("kid", sent.Patch, "plan", sent.ReviewRequired));

        Assert.Equal(PatchPreview.NeedsApproval, preview.Status);
        Assert.Contains("plan (in the child run) has planning.review: required", preview.Reasons.Single());
        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        var wait = parent.State.Waits.Single();
        Assert.Equal(("kid", true), (wait.StepId, wait.FromChild));
        Assert.Equal(PatchPreview.Applies, parent.PreviewChildPatch(new(childId, "plan", yaml, false)).Status);
        Assert.Equal(PatchPreview.Unavailable, parent.PreviewChildPatch(new("someone-else", "plan", yaml, false)).Status);
    }

    [Fact]
    public void The_approval_prompt_is_plain_text_within_8_kb_with_reason_reasons_resume_and_changes()
    {
        var huge = new string('x', 20_000);
        var core = Started("required");
        var patch = Read(Doc($"  - {{ op: insert, after: build, step: {{ id: lint, action: analysis.run, name: {huge} }} }}",
            reason: "needs a lint\u0007 step"));
        core.Apply(new StepCompleted("plan", "planned", Patch: patch));
        var prompt = core.Describe(core.State.Waits.Single()).Prompt;

        Assert.StartsWith("plan proposes a change to the plan (revision 1).\nReason: needs a lint step\nNeeds approval: plan has planning.review: required\n"
                          + "Continues at: build\nChanges:\n- op: insert\n  after: build\n  step:\n    id: lint\n", prompt, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(prompt) <= RunCore.MaxPatchPrompt);
        Assert.EndsWith("(cut: the full patch is in the run's record)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt, c => char.IsControl(c) && c != '\n');
    }
}
