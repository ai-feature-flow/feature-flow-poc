using FeatureFlow.Engine;
using Temporalio.Common;
using Temporalio.Worker;

namespace FeatureFlow.Replay.Tests;

/// <summary>
/// Recorded Temporal histories replay against the current workflow code (docs/09 section 4.1). A change to
/// workflow behaviour that a history took fails its replay with a nondeterminism error. There is no backward compatibility
/// before version 1 (LOG 263): such a change re-records the histories it breaks, never adds a patch marker. The histories
/// are first runs of integration tests, recorded with <c>FF_RECORD_HISTORIES</c> and kept in <c>tests/histories/</c>. No
/// Temporal server is needed.
/// </summary>
public class ReplayTests
{
    static string HistoryFolder { get; } = Path.Combine(FindRoot(), "tests", "histories");

    static string[] Names() => Directory.GetFiles(HistoryFolder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToArray();

    public static TheoryData<string> Histories() => new(Names());

    [Fact]
    public void The_recorded_histories_are_there()
    {
        // Without this, a missing folder would leave the theory with no cases and nothing replayed.
        Assert.Equal(["agent_question", "budget_middle", "decision_prompt", "delivery_tracked", "epic_delivery", "epic_delivery_child", "family_child", "family_parent", "project_session",
                "silent_parent_resumed", "sp1_parent", "sp1_parent_resumed", "steering_line", "steering_rerun_branch", "ticket_clean_up", "ticket_delivery", "wait_hand_over", "wait_hand_over_resumed", "wake"],
            Names());
    }

    /// <summary>
    /// Each history was recorded on the path it covers (review 8). The evidence is an activity, signal or update the path
    /// schedules or handles, so a change to those commands fails the history's replay; or text the path records (a
    /// <c>text</c> row), which proves only that the history took the path. Replay compares commands, not activity inputs:
    /// a change confined to an activity's input or a record's text is caught by the unit and integration tests, not here.
    /// </summary>
    [Theory]
    [InlineData("ticket_clean_up", "activity CleanUp")]
    [InlineData("ticket_clean_up", "text \"Declared\"")]
    [InlineData("family_parent", "activity EnsureWorkBranches")]
    [InlineData("family_parent", "activity ReadChild")]
    [InlineData("silent_parent_resumed", "activity WatchChildren")]
    [InlineData("delivery_tracked", "activity UpdateTracker")]
    [InlineData("budget_middle", "activity ReserveWorkItems")]
    [InlineData("decision_prompt", "text Approve to let the run go on")]
    [InlineData("steering_line", "update override")]
    [InlineData("steering_rerun_branch", "text \"Kind\":\"reopened\"")]
    [InlineData("wake", "signal wake")]
    [InlineData("project_session", "activity OpenSession")]
    [InlineData("project_session", "update session_check")]
    [InlineData("agent_question", "signal question_opened")]
    public void A_history_takes_the_path_it_covers(string name, string evidence)
    {
        var (kind, value) = (evidence[..evidence.IndexOf(' ')], evidence[(evidence.IndexOf(' ') + 1)..]);
        Assert.Contains(Evidence(name), e => e.Kind == kind && e.Value.Contains(value, StringComparison.Ordinal));
    }

    [Fact]
    public void A_resumed_history_starts_with_an_interrupted_wait_to_resume()
    {
        // A wait interrupted at a hand-over and resumed in the next run (LOG 139).
        Assert.True(StartsResuming("wait_hand_over_resumed"), "wait_hand_over_resumed does not start with an interrupted step to resume");
    }

    /// <summary>Whether a recorded run started from a hand-over carrying a step to resume (LOG 139).</summary>
    static bool StartsResuming(string name)
    {
        using var history = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(HistoryFolder, name + ".json")));
        var data = history.RootElement.GetProperty("events")[0].GetProperty("workflowExecutionStartedEventAttributes")
            .GetProperty("input").GetProperty("payloads")[0].GetProperty("data").GetString()!;
        var start = System.Text.Json.JsonSerializer.Deserialize<FeatureFlow.Interpreter.Wire.RunStart>(Convert.FromBase64String(data))!;
        return start.Carried?.Core.Pending.Any(e => e.Resumes) == true;
    }

    /// <summary>
    /// What a history shows of its path: <c>activity</c>, <c>signal</c> and <c>update</c> names, and <c>text</c> for each
    /// payload, decoded, that a scheduled activity was given (for example a record of the timeline).
    /// </summary>
    static List<(string Kind, string Value)> Evidence(string name)
    {
        using var history = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(HistoryFolder, name + ".json")));
        var found = new List<(string Kind, string Value)>();
        foreach (var e in history.RootElement.GetProperty("events").EnumerateArray())
        {
            if (e.TryGetProperty("activityTaskScheduledEventAttributes", out var activity))
            {
                found.Add(("activity", activity.GetProperty("activityType").GetProperty("name").GetString()!));
                if (activity.TryGetProperty("input", out var input))
                    foreach (var payload in input.GetProperty("payloads").EnumerateArray())
                        if (payload.TryGetProperty("data", out var data))
                            found.Add(("text", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!))));
            }

            if (e.TryGetProperty("workflowExecutionSignaledEventAttributes", out var signal))
                found.Add(("signal", signal.GetProperty("signalName").GetString()!));
            if (e.TryGetProperty("workflowExecutionUpdateAcceptedEventAttributes", out var update))
                found.Add(("update", update.GetProperty("acceptedRequest").GetProperty("input").GetProperty("name").GetString()!));
        }

        return found;
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task A_recorded_history_replays_against_the_current_workflow(string name)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(HistoryFolder, name + ".json"), TestContext.Current.CancellationToken);
        // Replay checks determinism, not timing: debug mode turns off the 2 s deadlock detector, which fired on a
        // loaded CI runner (docs/build/m4-results.md follow-up 14).
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions { DebugMode = true }.AddWorkflow<RunWorkflow>());

        var result = await replayer.ReplayWorkflowAsync(WorkflowHistory.FromJson(WorkflowIdOf(json), json), throwOnReplayFailure: false, TestContext.Current.CancellationToken);

        Assert.True(result.ReplayFailure is null, $"{name} does not replay: {result.ReplayFailure}");
    }

    /// <summary>
    /// The workflow id the history ran under, which child workflow ids are built from: the run id in the
    /// workflow's input, which is its workflow id.
    /// </summary>
    static string WorkflowIdOf(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("events")[0].GetProperty("workflowExecutionStartedEventAttributes")
            .GetProperty("input").GetProperty("payloads")[0].GetProperty("data").GetString()!;
        using var input = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(data));
        return input.RootElement.GetProperty("RunId").GetString()!;
    }

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found above the test output folder");
    }
}
