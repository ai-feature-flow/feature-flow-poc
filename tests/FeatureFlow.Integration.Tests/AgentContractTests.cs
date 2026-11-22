using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP3's tasks as contract tests of the Claude Code runtime with the engine's tools (docs/spikes/sp3-results.md):
/// report, correct, wait, and stop. The model is scripted, so these test the runtime's and the engine's side of the
/// contract through the real CLI in a sandbox; the runtime contract suite (F12) covers both runtimes without one, and
/// the Live category runs the same tasks with a real model.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentContractTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    static string Key(string runId) => $"{runId}/implement/1";

    /// <summary>SP3 T2: a rejected report comes back with its errors, and a corrected one is accepted in the same session.</summary>
    [Fact]
    public async Task A_rejected_report_is_corrected_within_the_correction_turns()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? rejection = null;
        model.ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Notes\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report(answer: null))
            .Then(c =>
            {
                rejection = c.LastToolResult;
                return ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report());
            })
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("correct", model);
        var runId = h.Start("T-2", "Write notes.md and report.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        Assert.Contains("output 'answer' is required", rejection);
        Assert.Contains("correction turn 1 of 2", rejection);
        var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId));
        Assert.Equal([false, true], reports.Select(r => r.Accepted));
    }

    /// <summary>
    /// LOG 246 through the real runtime: the agent reports notes without the heading their type requires. The engine accepts
    /// the report's shape, checks the stored file, and continues the same Claude Code session with the error; the model,
    /// given the error as its next message, fixes the file and reports again; the run completes with the corrected file.
    /// </summary>
    [Fact]
    public async Task An_artifact_that_breaks_its_type_is_corrected_in_a_continued_session()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Notes without a heading\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.")
            .ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "# Summary\n\nNotes with their heading.\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("artifact-check", model);
        var runId = h.Start("T-246", "Write notes.md and report.", "m4:templates/checked_step");
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        Assert.Equal([true, false, true], (await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Select(r => r.Accepted));
        // The continued session's turn is the engine's message, naming the file and the rule.
        Assert.Contains(model.Requests, r => System.Text.RegularExpressions.Regex.Unescape(r.ToJsonString())
            .Contains("artifact 'notes' (notes.md): heading 'Summary' is required", StringComparison.Ordinal));
        var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, Key(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
        Assert.Equal(["1 started", "1 ended completed", "2 started", "2 ended completed"], sessions);
    }

    /// <summary>DR17: the output folder the engine created is the agent's and empty before the agent does anything.</summary>
    [Fact]
    public async Task The_output_folder_is_the_agent_s_and_empty_before_it_starts()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? folder = null;
        model.ThenBash("stat -c '%U %F' /ff/out; ls -A /ff/out | wc -l")
            .Then(c =>
            {
                folder = c.LastToolResult;
                return ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", "Notes\n"));
            })
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("output-folder", model);
        var runId = h.Start("T-3", "Write notes.md and report.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        Assert.Equal("node directory\n0", folder?.Trim());
        Assert.True(Assert.Single(await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Accepted);
    }

    /// <summary>SP3 T4: the agent asks, the person answers through the API, and the agent uses the answer.</summary>
    [Fact]
    public async Task A_question_waits_for_the_answer_given_through_the_api()
    {
        var nonce = "teal-" + Random.Shared.Next(10000, 99999);
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenTool("mcp__engine__ask_human", new JsonObject { ["question"] = "Which colour?", ["options"] = new JsonArray("red", "blue") })
            .Then(c => ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", c.LastToolResult + "\n")))
            .Then(c => ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report(answer: AgentScripts.Answer(c))))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("question", model);
        var runId = h.Start("T-4", "Ask which colour, then report the answer.");
        var wait = await h.WaitForWaitAsync(runId, "question", RunTimeout);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var (exit, _, error) = h.Cli("answer", runId, wait, nonce);
        Assert.True(exit == 0, error);
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        var question = Assert.Single(await h.RecordsAsync<QuestionRecord>(Queries.Questions, Key(runId)));
        Assert.Equal(QuestionRecord.Answered, question.Status);
        var report = (await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Last(r => r.Accepted).Report!;
        Assert.Equal($"\"{nonce}\"", report.Outputs["answer"]);
        await h.RecordHistoryAsync(runId, "agent_question", model.Credential);
    }

    /// <summary>
    /// A question no one answers times out at its step's <c>questions.timeout</c>: the core closes it when its timer fires, the
    /// agent is told no answer came and carries on (docs/04 section 3.4; LOG 18), and the record shows it timed out.
    /// </summary>
    [Fact]
    public async Task A_question_no_one_answers_times_out_and_the_agent_carries_on()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenTool("mcp__engine__ask_human", new JsonObject { ["question"] = "Which colour?", ["options"] = new JsonArray("red", "blue") })
            .Then(c => ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", c.LastToolResult + "\n")))
            .Then(c => ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report(answer: "none")))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("question-timeout", model);
        var runId = h.Start("T-5", "Ask which colour, then report.", "m4:templates/question_timeout");
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        var question = Assert.Single(await h.RecordsAsync<QuestionRecord>(Queries.Questions, Key(runId)));
        Assert.Equal(QuestionRecord.TimedOut, question.Status);
        Assert.Contains("question_timed_out", timeline);
        await h.RecordHistoryAsync(runId, "question_timeout", model.Credential);
    }

    /// <summary>
    /// SP3 N2 and the stop rule: every report rejected. After the correction turns the engine says the attempt has
    /// failed, the agent stops, and the attempt fails as a work failure (docs/04 section 3.1, step 5).
    /// </summary>
    [Fact]
    public async Task Reports_still_invalid_after_the_correction_turns_fail_the_attempt()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        var results = new List<string>();
        model.ThenTool("mcp__engine__report", AgentScripts.Report(outcome: "done"));
        for (var i = 0; i < 3; i++)
            model.Then(c =>
            {
                results.Add(c.LastToolResult);
                return c.LastToolResult.Contains("attempt has failed", StringComparison.Ordinal)
                    ? new JsonObject { ["type"] = "text", ["text"] = "I could not get the report accepted." }
                    : ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report(outcome: "done"));
            });
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("stop", model);
        var runId = h.Start("T-5", "Report.");
        await h.WaitForWaitAsync(runId, "intervention", RunTimeout);

        var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId));
        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.False(r.Accepted));
        Assert.Contains("attempt has failed", results[2]);
        Assert.Contains("the report was rejected 3 times", await h.TimelineAsync(runId));
    }

    /// <summary>
    /// Agent planning through the real runtime (design note s1, s3): the agent reads its plan with <c>inspect_plan</c>, proposes an
    /// inserted step with <c>propose_patch</c>, and reports. With <c>review: required</c> the run opens a patch approval at the step,
    /// its prompt carrying the agent's reason; a person approves it through the CLI, and the inserted step runs.
    /// </summary>
    [Fact]
    public async Task An_agent_s_proposed_step_waits_for_approval_and_then_runs()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? plan = null, staged = null;
        var migrate = new JsonObject
        {
            ["id"] = "migrate",
            ["agent"] = new JsonObject { ["role"] = "std:roles/implementer", ["task"] = "m4:tasks/agent_task" },
            ["with"] = new JsonObject { ["ticket_id"] = "T-6", ["instruction"] = "Write notes.md and report." },
            ["produces"] = new JsonObject { ["implemented"] = new JsonArray(new JsonObject { ["name"] = "notes", ["type"] = "m4:artifact_types/notes" }) },
            ["outputs"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string" } },
            ["outcomes"] = new JsonObject { ["implemented"] = "success" },
            ["routes"] = new JsonObject { ["implemented"] = new JsonObject { ["end"] = "completed" } },
        };
        model.ThenTool("mcp__engine__inspect_plan", new JsonObject())
            .Then(c =>
            {
                plan = c.LastToolResult;
                return ScriptedModel.ToolUse("mcp__engine__propose_patch", new JsonObject
                {
                    ["base_revision"] = JsonNode.Parse(plan)!["revision"]!.GetValue<int>(),
                    ["resume_at"] = "migrate",
                    ["reason"] = "the schema needs a migration step",
                    ["changes"] = new JsonArray(new JsonObject { ["op"] = "insert", ["after"] = "implement", ["step"] = migrate.DeepClone() }),
                });
            })
            .Then(c =>
            {
                staged = c.LastToolResult;
                return ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", "Notes\n"));
            })
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.")
            // The inserted step's own session.
            .ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Migration notes\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("planning", model);
        var runId = h.Start("T-6", "Write notes.md and report.", "m4:templates/planning_step");
        var approval = await h.WaitForWaitAsync(runId, "patch_approval", RunTimeout);
        var prompt = (await h.WaitsAsync(runId, "patch_approval")).Single()["prompt"]!.ToString();
        var (exit, output, error) = h.Cli("answer", runId, approval, "approve");
        Assert.True(exit == 0, output + error);
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        Assert.Contains("\"you\"", plan);
        Assert.Contains("Staged against revision", staged);
        Assert.Contains("the schema needs a migration step", prompt);
        Assert.Contains(model.Requests, r => System.Text.RegularExpressions.Regex.Unescape(r.ToJsonString()).Contains("## Planning authority", StringComparison.Ordinal));
        Assert.NotNull(Assert.Single(await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Report!.Patch);
        Assert.True(Assert.Single(await h.RecordsAsync<ReportRecord>(Queries.Reports, $"{runId}/migrate/1")).Accepted);
        await h.RecordHistoryAsync(runId, "agent_planning_patch", model.Credential);
    }
}
