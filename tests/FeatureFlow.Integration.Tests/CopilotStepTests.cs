using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Client;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M8's Copilot CLI runtime in a Docker sandbox: the real Copilot CLI, the real sandbox, model proxy and engine tools,
/// with a scripted model behind the proxy, as M4's Claude Code tests run. Copilot reaches the model as a custom
/// provider of the Messages API with only the step's token (LOG 293).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CopilotStepTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    static string StepKey(string runId) => $"{runId}/implement/1";

    /// <summary>
    /// An implementer step on Copilot CLI commits, cannot remove, replace or switch off the engine's hooks, is denied a push
    /// by the hook (its settings are the engine's), reports
    /// through the engine's tool, and its commit reaches the work branch. Copilot's MCP tools are named <c>engine-*</c>.
    /// </summary>
    [Fact]
    public async Task A_copilot_step_commits_is_denied_a_push_and_reports_through_the_engine()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? pushResult = null, tamperResult = null;
        model.Then(_ => Bash("cd /ff/work && echo a > a.txt && git add -A && git commit -q -m 'Add a'"))
            // Review R2, F2: the agent tries to remove or replace the engine's hooks, then to switch them off, before it pushes.
            .Then(_ => Bash("rm -f /ff/copilot/settings.json; mv /ff/copilot/settings.json /ff/copilot/old.json; echo '{}' > /ff/copilot/settings.json; " +
                            "echo '{\"disableAllHooks\":true}' > /ff/copilot/settings.local.json; echo '--- settings'; cat /ff/copilot/settings.json"))
            .Then(c =>
            {
                tamperResult = c.LastToolResult;
                return Bash("cd /ff/work && git push origin HEAD");
            })
            .Then(c =>
            {
                pushResult = c.LastToolResult;
                return ScriptedModel.ToolUse("create", new JsonObject { ["path"] = "/ff/out/notes.md", ["file_text"] = "Added a\n" });
            })
            .Then(_ => ScriptedModel.ToolUse("engine-report", AgentScripts.Report()))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("copilot", model, new AgentHarnessOptions { Runtime = "copilot_cli" });
        var runId = h.Start("T-51", "Add a.txt.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);

        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.Contains("never push", pushResult);
        // The engine's settings survived the attempt, hooks and all.
        Assert.NotNull(tamperResult);
        Assert.Contains("--- settings", tamperResult);
        var settings = tamperResult[(tamperResult.IndexOf("--- settings", StringComparison.Ordinal) + 1)..];
        Assert.Contains("/ff/bin/ff-sandbox hook pre_tool_use copilot", settings);
        var events = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId));
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash deny", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("engine-report allow", StringComparison.Ordinal));
        Assert.Contains("session 1 started on runtime copilot_cli", timeline);
        Assert.Equal("Add a", await h.RemoteMessageAsync((await h.RemoteRevisionAsync("refs/heads/ff/ticket/T-51"))!));
        Assert.True(model.Accepted > 0 && model.Refused == 0, $"the model saw {model.Accepted} requests with the proxy's credential and {model.Refused} without");
        Assert.All(model.Requests, r => Assert.Equal("claude-haiku-4-5", r["model"]?.ToString()));
    }

    static readonly string[] CredentialNames = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_PROVIDER_BEARER_TOKEN"];

    /// <summary>
    /// SP5 C2 to C4 for Copilot CLI (docs/spikes/sp5-results.md): no model or GitHub credential in the environment of any
    /// process the agent's user can read, its home or its transcript; the home's startup files are the engine's, empty
    /// (DR19); every model request reaches the model with the proxy's credential.
    /// </summary>
    [Fact]
    public async Task The_copilot_sandbox_holds_no_model_or_github_credential()
    {
        var credential = "model-key-" + Guid.NewGuid().ToString("N");
        await using var model = new ScriptedModel(credential);
        model.Then(_ => Bash("{ env | sort; echo '--- processes'; cat /proc/[0-9]*/environ 2>/dev/null | tr '\\0' '\\n' | sort -u; " +
                             "echo '--- home'; ls -A \"$HOME\"; echo '--- startup bytes'; " +
                             "cat \"$HOME/.profile\" \"$HOME/.bashrc\" \"$HOME/.bash_profile\" \"$HOME/.zshenv\" \"$HOME/.zshrc\" | wc -c; } > /ff/out/notes.md"))
            .Then(_ => ScriptedModel.ToolUse("engine-report", AgentScripts.Report()))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("copilot-sp5", model, new AgentHarnessOptions { Runtime = "copilot_cli" });
        var runId = h.Start("T-52", "Record your environment in notes.md.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");

        var notes = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "artifact notes: "));
        var transcript = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "transcript of session 1: "));
        // The agent's own view is real: the proxy's URL and the step's token as the custom provider's key.
        Assert.Contains("COPILOT_PROVIDER_BASE_URL=http://host.docker.internal:", notes);
        Assert.Contains("COPILOT_PROVIDER_API_KEY=ffs1.", notes);
        Assert.Contains("COPILOT_OFFLINE=true", notes);
        Assert.Contains("HOME=/ff/home", notes);
        foreach (var name in CredentialNames)
            Assert.DoesNotContain(name + "=", notes);
        Assert.Contains("--- startup bytes\n0", notes);
        foreach (var secret in new[] { credential, Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") }.OfType<string>().Where(s => s.Length > 8))
        {
            Assert.False(notes.Contains(secret, StringComparison.Ordinal), "a credential is in the agent's environment");
            Assert.False(transcript.Contains(secret, StringComparison.Ordinal), "a credential is in the transcript");
        }

        Assert.True(model.Accepted >= 2 && model.Refused == 0, $"accepted {model.Accepted}, refused {model.Refused}");
    }

    /// <summary>
    /// SP5 N1 for Copilot CLI, made resumable: with the model unreachable the CLI gives up after its own retries, the
    /// engine classifies the session as stopped on the model (infrastructure), and the step uses no attempt and does not
    /// succeed. When the model is back, a retry continues the session in the same sandbox and the step completes.
    /// </summary>
    [Fact]
    public async Task With_the_model_unreachable_the_copilot_step_stops_resumably_rather_than_succeeding()
    {
        var port = ScriptedModel.FreePort();
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"), port);
        model.Otherwise = c => c.LastToolResult.Contains("accepted", StringComparison.Ordinal)
            ? new JsonObject { ["type"] = "text", ["text"] = "Done." }
            : c.Body.ToJsonString().Contains("Created file", StringComparison.Ordinal)
                ? ScriptedModel.ToolUse("engine-report", AgentScripts.Report())
                : ScriptedModel.ToolUse("create", new JsonObject { ["path"] = "/ff/out/notes.md", ["file_text"] = "Notes\n" });
        await using var h = await AgentHarness.StartAsync("copilot-down", model, new AgentHarnessOptions { Runtime = "copilot_cli" });
        var runId = h.Start("T-53", "Write notes.md and report.");

        var deadline = DateTime.UtcNow + RunTimeout;
        Temporalio.Api.Workflow.V1.PendingActivityInfo? pending = null;
        while (DateTime.UtcNow < deadline)
        {
            pending = (await h.Host.Client.WorkflowService.DescribeWorkflowExecutionAsync(new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
            {
                Namespace = h.Host.Client.Options.Namespace,
                Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = runId },
            })).PendingActivities.FirstOrDefault();
            if (pending is { Attempt: >= 2 })
                break;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(pending);
        Assert.True(pending!.Attempt >= 2, $"attempt {pending.Attempt}");
        Assert.Contains("stopped resumably", pending.LastFailure.Message);
        Assert.Equal("infrastructure", pending.LastFailure.ApplicationFailureInfo.Type);
        Assert.Equal("running", (await h.ShowAsync(runId))["run"]!["status"]!.ToString());
        Assert.DoesNotContain("attempt_failed", await h.TimelineAsync(runId));

        await model.StartAsync();
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        await AgentScripts.AssertOneSandboxAsync(h, StepKey(runId));
        var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
        Assert.Contains("1 ended model_unreachable", sessions);
        Assert.Contains(sessions, s => s.EndsWith("ended completed", StringComparison.Ordinal));
    }

    /// <summary>
    /// SP5 N2 for Copilot CLI: the proxy's key is one the model refuses. The CLI cannot fall back to another credential
    /// (it has none, and is offline), so the step does not succeed: it stops for a person as a configuration fault.
    /// </summary>
    [Fact]
    public async Task With_a_key_the_model_refuses_the_copilot_step_stops_for_a_person()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.Then(_ => ScriptedModel.ToolUse("engine-report", AgentScripts.Report())).ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("copilot-refused", model, new AgentHarnessOptions { Runtime = "copilot_cli", ModelKey = "not-the-model-key" });
        var runId = h.Start("T-54", "Write notes.md and report.");

        await h.WaitForWaitAsync(runId, "intervention", RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.Contains("the model refused the step's requests", timeline);
        Assert.Equal(0, model.Accepted);
        Assert.True(model.Refused > 0);
    }

    static JsonObject Bash(string command) => ScriptedModel.ToolUse("bash", new JsonObject { ["command"] = command, ["description"] = "scripted" });
}
