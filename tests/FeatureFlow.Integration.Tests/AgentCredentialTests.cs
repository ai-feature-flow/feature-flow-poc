using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Client;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP5's checks as tests (docs/spikes/sp5-results.md): the sandbox holds no model credential in its environment,
/// home or transcript (F6, DR19), every model request goes through the proxy, and with the model unreachable the
/// step stops resumably rather than succeeding.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentCredentialTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    static string Key(string runId) => $"{runId}/implement/1";

    static readonly string[] CredentialNames = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"];

    [Fact]
    public async Task The_sandbox_holds_no_model_credential_in_its_environment_home_or_transcript()
    {
        var credential = "model-key-" + Guid.NewGuid().ToString("N");
        await using var model = new ScriptedModel(credential);
        // Claude Code removes its own credential variables from its tools' shells, so env alone would not show one
        // given to the runtime: every process environment the agent's user can read is listed too.
        model.ThenBash("{ env | sort; echo '--- processes'; cat /proc/[0-9]*/environ 2>/dev/null | tr '\\0' '\\n' | sort -u; " +
                       "echo '--- home'; ls -A \"$HOME\"; echo '--- startup bytes'; " +
                       "cat \"$HOME/.profile\" \"$HOME/.bashrc\" \"$HOME/.bash_profile\" \"$HOME/.zshenv\" \"$HOME/.zshrc\" | wc -c; } > /ff/out/notes.md")
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("credential", model);
        var runId = h.Start("T-8", "Record your environment in notes.md.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");

        var report = (await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Last(r => r.Accepted);
        var notes = await NotesAsync(h, runId);
        var transcript = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "transcript of session 1: "));
        // The agent's own view of its environment is real: it holds the proxy's URL and the step's token.
        Assert.Contains("ANTHROPIC_BASE_URL=http://host.docker.internal:", notes);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN=ffs1.", notes);
        Assert.Contains("HOME=/ff/home", notes);
        // C2: no credential's name or value in the environment; DR19: the home's startup files are the engine's, empty.
        foreach (var name in CredentialNames)
            Assert.DoesNotContain(name, notes);
        Assert.Contains("--- startup bytes\n0", notes);
        // C4: the credential appears nowhere the agent or its transcript reached, nor does an operator's own token.
        foreach (var secret in new[] { credential, Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") }.OfType<string>().Where(s => s.Length > 8))
        {
            Assert.False(notes.Contains(secret, StringComparison.Ordinal), "a credential is in the agent's environment");
            Assert.False(transcript.Contains(secret, StringComparison.Ordinal), "a credential is in the transcript");
        }

        // C3: every model request the agent made reached the model with the proxy's credential, none without it.
        Assert.True(model.Accepted >= 2 && model.Refused == 0, $"accepted {model.Accepted}, refused {model.Refused}");
        Assert.True(report.Accepted);
    }

    /// <summary>The notes artifact the step copied out, named on the timeline.</summary>
    static async Task<string> NotesAsync(AgentHarness h, string runId) =>
        await h.ArtifactTextAsync(AgentScripts.Reference(await h.TimelineAsync(runId), "artifact notes: "));

    /// <summary>
    /// SP5 N1, made resumable: with the model unreachable the session stops on an API error, which the engine
    /// classifies as infrastructure. The step uses no attempt and does not succeed; Temporal retries it, each retry
    /// continuing the session in the same sandbox. When the model is reachable again, the step completes.
    /// </summary>
    [Fact]
    public async Task With_the_model_unreachable_the_step_stops_resumably_rather_than_succeeding()
    {
        var port = ScriptedModel.FreePort();
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"), port);
        model.Otherwise = c => c.Body.ToJsonString().Contains("File created successfully", StringComparison.Ordinal)
            ? c.LastToolResult.Contains("accepted", StringComparison.Ordinal)
                ? new JsonObject { ["type"] = "text", ["text"] = "Done." }
                : ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report())
            : ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", "Notes\n"));
        await using var h = await AgentHarness.StartAsync("unreachable", model);
        var runId = h.Start("T-9", "Write notes.md and report.");

        // The step retries while the model is unreachable: several activity attempts, each an infrastructure failure.
        var deadline = DateTime.UtcNow + RunTimeout;
        Temporalio.Api.Workflow.V1.PendingActivityInfo? pending = null;
        while (DateTime.UtcNow < deadline)
        {
            pending = (await h.Host.Client.WorkflowService.DescribeWorkflowExecutionAsync(new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
            {
                Namespace = h.Host.Client.Options.Namespace,
                Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = runId },
            })).PendingActivities.FirstOrDefault();
            if (pending is { Attempt: >= 3 })
                break;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(pending);
        Assert.True(pending!.Attempt >= 3, $"attempt {pending.Attempt}");
        Assert.Contains("stopped resumably", pending.LastFailure.Message);
        Assert.Equal("infrastructure", pending.LastFailure.ApplicationFailureInfo.Type);
        var view = await h.ShowAsync(runId);
        Assert.Equal("running", view["run"]!["status"]!.ToString());
        Assert.Empty(view["waits"]!.AsArray());
        Assert.DoesNotContain("attempt_failed", await h.TimelineAsync(runId));

        // The model comes back: the next retry continues the session and the step completes.
        await model.StartAsync();
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.Single(timeline.Split('\n'), l => l.StartsWith("started implement", StringComparison.Ordinal));
        await AgentScripts.AssertOneSandboxAsync(h, Key(runId));
        // Each retry continued the session: a session that stopped on the model is followed by another.
        var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, Key(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
        Assert.Contains("1 ended model_unreachable", sessions);
        Assert.Contains(sessions, s => s.EndsWith("ended completed", StringComparison.Ordinal));
    }
}
