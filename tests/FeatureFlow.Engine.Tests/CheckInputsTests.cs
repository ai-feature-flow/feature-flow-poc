using FeatureFlow.Engine;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// Pre-submit parity's digest (docs/09 section 4.2): what a step's activity is given, apart from who it is. The same task,
/// tools, model and commit give the same digest; a change in any gives another.
/// </summary>
public class CheckInputsTests
{
    static readonly StepContract Contract = new([new OutcomeSpec("passed", "success")], new Dictionary<string, string>(), [], new Dictionary<string, string>());

    static StepCall Call(string key = "run-1/review/1", string task = "Review the spec.", string role = "std:roles/reviewer", IReadOnlyList<string>? tools = null,
        string spec = "sha256:aaa") => new(
        new StepIdentity("run-1", "t", "review", 1, key), "agent", role, new Dictionary<string, string> { ["focus"] = "\"api\"" }, Contract, false, null,
        new AgentStepSpec("none", tools ?? ["read", "search"], task, "std", [], 2, 0, TimeSpan.FromMinutes(5), false, "fail", "project",
            new Dictionary<string, string> { ["acme/api"] = "ff/project/x" }, new Dictionary<string, string>(), new Dictionary<string, string> { ["spec"] = spec },
            "wf", "lock"));

    static readonly ActionScope Scope = new(["acme/api"], "acme/api", "ff/project/x", null, [new ArtifactEntry("define", "requirements", "std:artifact_types/requirements", "sha256:r1")],
        [], [], new Dictionary<string, string> { ["requirements"] = "sha256:r1" });

    [Fact]
    public void The_same_task_tools_model_and_commit_give_the_same_digest_whoever_the_step_is()
    {
        Assert.Equal(CheckInputs.Digest(Call(), Scope), CheckInputs.Digest(Call(key: "run-1/review/check-c1"), Scope));
        // A map's order does not matter.
        var reordered = Scope with { Consumed = new Dictionary<string, string> { ["requirements"] = "sha256:r1" } };
        Assert.Equal(CheckInputs.Digest(Call(), Scope), CheckInputs.Digest(Call(), reordered));
    }

    [Fact]
    public void A_different_task_tools_model_or_commit_gives_another_digest()
    {
        var digest = CheckInputs.Digest(Call(), Scope);
        Assert.NotEqual(digest, CheckInputs.Digest(Call(task: "Review the plan."), Scope));
        Assert.NotEqual(digest, CheckInputs.Digest(Call(tools: ["read", "search", "shell"]), Scope));
        // The model follows from the role (role_mapping, docs/07 section 3).
        Assert.NotEqual(digest, CheckInputs.Digest(Call(role: "std:roles/planner"), Scope));
        // The commit: the files it was given, by content reference.
        Assert.NotEqual(digest, CheckInputs.Digest(Call(spec: "sha256:bbb"), Scope));
        Assert.NotEqual(digest, CheckInputs.Digest(Call(), Scope with { Consumed = new Dictionary<string, string> { ["requirements"] = "sha256:r2" } }));
    }
}
