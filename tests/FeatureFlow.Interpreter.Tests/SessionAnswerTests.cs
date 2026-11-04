using FeatureFlow.Interpreter;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>A decision answered by a session's submission holds the artifacts its outcome promises, exactly (docs/04 section 4.4).</summary>
public class SessionAnswerTests
{
    const string Template = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: session, level: project }
        artifact_types: [std:artifact_types/requirements]
        steps:
          - id: define
            decision:
              type: project.requirements_session
              session:
                repository: acme/api
                files: [{ path: requirements.yaml, name: requirements, type: std:artifact_types/requirements }]
            produces: { submitted: [{ name: requirements, type: std:artifact_types/requirements }] }
            outcomes: { submitted: success, abandoned: failure }
            routes: { submitted: { end: completed }, abandoned: { end: rejected } }
        """;

    static readonly Probe Lib = new("sessions", ("session", Template));

    static RunCore Opened()
    {
        var core = Lib.Start("session");
        core.Advance();
        return core;
    }

    [Fact]
    public void A_submission_with_the_promised_artifacts_is_the_steps_and_ends_its_wait()
    {
        var core = Opened();
        var answer = new DecisionAnswered("define", "submitted", new Dictionary<string, string> { ["requirements"] = "sha256:r" });
        Assert.Null(core.Check(answer));
        Assert.True(core.Apply(answer).Accepted);
        Assert.Equal("sha256:r", core.State.Steps["define"].Current!.Artifacts["requirements"]);
        Assert.Empty(core.State.Waits);
    }

    [Theory]
    [InlineData("other", "artifact 'other' is not declared for outcome 'submitted'")]
    [InlineData(null, "promises artifact 'requirements', which was not submitted")]
    public void A_submission_with_other_artifacts_is_refused(string? name, string error)
    {
        var core = Opened();
        var given = name is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [name] = "sha256:x" };
        var answer = new DecisionAnswered("define", "submitted", given);
        Assert.Contains(error, core.Check(answer));
        var result = core.Apply(answer);
        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains(error, StringComparison.Ordinal));
        Assert.Single(core.State.Waits);
    }
}
