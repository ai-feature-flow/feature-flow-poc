using System.Reflection;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using Temporalio.Workflows;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The run workflow's queries for an agent with planning authority (design note, agent planning, s3): <c>agent_plan</c>,
/// <c>patch_preview</c> and <c>child_patch_preview</c> are registered under their wire names, answer from the core, and answer
/// <c>unavailable</c> before the core exists.
/// </summary>
public class AgentPlanQueryTests
{
    const string Template = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: planned, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [self], review: required }
            outcomes: { planned: success, blocked: blocked }
          - id: build
            action: test.run
            routes: { passed: { end: completed } }
        """;

    const string Patch = """
        api_version: feature-flow/v1
        kind: patch
        metadata: { proposed_by: { type: agent, step_id: plan }, reason: lint first }
        base_revision: 1
        resume_at: build
        changes:
          - { op: insert, before: build, step: { id: lint, action: analysis.run } }
        """;

    static RunWorkflow Workflow(bool withCore)
    {
        var workflow = new RunWorkflow(new RunStart("run-1", "probe:templates/planned", ["acme/api"], new Dictionary<string, string>(), "lock", false, "someone"));
        if (!withCore)
            return workflow;
        var root = Path.Combine(AppContext.BaseDirectory, "agent-plan-probe");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        File.WriteAllText(Path.Combine(root, "templates", "planned.yaml"), Template);
        var d = new DiagnosticBag();
        var libraries = new LibrarySet(Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), d)!, Library.Library.Open(root, d)!);
        var (core, diagnostics) = RunCore.Start(libraries, new LibraryReference("probe", "templates", "planned"),
            new RunInfo("run-1", ["acme/api"], new Dictionary<string, YNode>()));
        Assert.True(core is not null, string.Join("\n", diagnostics));
        core!.Advance();
        typeof(RunWorkflow).GetField("core", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(workflow, core);
        return workflow;
    }

    [Fact]
    public void The_three_queries_are_registered_under_their_wire_names()
    {
        var definition = WorkflowDefinition.Create(typeof(RunWorkflow));

        foreach (var name in new[] { RunWorkflowNames.AgentPlan, RunWorkflowNames.PatchPreview, RunWorkflowNames.ChildPatchPreview })
            Assert.True(definition.Queries.ContainsKey(name), $"no query {name}");
    }

    [Fact]
    public void The_queries_answer_from_the_core()
    {
        var workflow = Workflow(withCore: true);

        var plan = workflow.AgentPlan(new AgentPlanQuery("run-1/plan/1", "self"));
        var preview = workflow.PatchPreview(new PatchPreviewQuery("run-1/plan/1", Patch, ToParent: false));
        var child = workflow.ChildPatchPreview(new ChildPatchPreviewQuery("run-1.kid.1", "plan", Patch, false));

        Assert.Equal((AgentPlanView.Ok, "plan"), (plan.Status, plan.You!.StepId));
        Assert.Equal(PatchPreview.NeedsApproval, preview.Status);
        Assert.Contains("plan has planning.review: required", preview.Reasons.Single());
        Assert.Equal(PatchPreview.Unavailable, child.Status);
    }

    [Fact]
    public void Before_the_core_exists_every_query_answers_unavailable()
    {
        var workflow = Workflow(withCore: false);

        Assert.Equal(AgentPlanView.Unavailable, workflow.AgentPlan(new AgentPlanQuery("run-1/plan/1", "self")).Status);
        Assert.Equal(PatchPreview.Unavailable, workflow.PatchPreview(new PatchPreviewQuery("run-1/plan/1", Patch, false)).Status);
        Assert.Equal(PatchPreview.Unavailable, workflow.ChildPatchPreview(new ChildPatchPreviewQuery("c", "plan", Patch, false)).Status);
    }
}
