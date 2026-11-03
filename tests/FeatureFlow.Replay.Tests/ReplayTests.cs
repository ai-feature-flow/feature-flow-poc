using FeatureFlow.Engine;
using Temporalio.Common;
using Temporalio.Worker;

namespace FeatureFlow.Replay.Tests;

/// <summary>
/// Recorded Temporal histories replay against the current workflow code (docs/09 section 4.1). A change to
/// workflow behaviour that an old history did not take must be wrapped in a patch marker
/// (<c>Workflow.Patched</c>), or these fail with a nondeterminism error. The histories are the first runs of the
/// M3 integration tests and of M4's question test (an agent step that asks, a question signal, an answer), recorded with <c>FF_RECORD_HISTORIES</c> and kept in <c>tests/histories/</c>. No
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
        Assert.Equal(["agent_question", "budget_middle", "delivery_tracked", "epic_delivery", "epic_delivery_child", "sp1_parent", "sp1_parent_resumed",
            "ticket_clean_up", "ticket_delivery"], Names());
    }

    [Fact]
    public void A_history_holds_each_M5_patch_marker()
    {
        // M5's markers: the clean_up reset and the step start (check 32, an action's scope). Without a history holding
        // them, a change to either path would replay green (review 8).
        using var history = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(HistoryFolder, "ticket_clean_up.json")));
        var patches = history.RootElement.GetProperty("events").EnumerateArray()
            .Where(e => e.TryGetProperty("markerRecordedEventAttributes", out _))
            .SelectMany(e => e.GetProperty("markerRecordedEventAttributes").GetProperty("details").EnumerateObject())
            .SelectMany(d => d.Value.GetProperty("payloads").EnumerateArray())
            .Select(p => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p.GetProperty("data").GetString()!)))
            .ToList();

        Assert.Contains(patches, p => p.Contains($"\"{RunWorkflow.CleanUpPatch}\"", StringComparison.Ordinal));
        Assert.Contains(patches, p => p.Contains($"\"{RunWorkflow.StepStartPatch}\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("delivery_tracked", RunWorkflow.TrackerUpdatesPatch)]
    [InlineData("budget_middle", RunWorkflow.WorkItemsPatch)]
    public void A_history_holds_each_M6_delivery_patch_marker(string name, string marker)
    {
        // Declarative tracker updates and the work-item budget (M6): without a history holding each marker, a change to
        // either path would replay green.
        using var history = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(HistoryFolder, name + ".json")));
        var patches = history.RootElement.GetProperty("events").EnumerateArray()
            .Where(e => e.TryGetProperty("markerRecordedEventAttributes", out _))
            .SelectMany(e => e.GetProperty("markerRecordedEventAttributes").GetProperty("details").EnumerateObject())
            .SelectMany(d => d.Value.GetProperty("payloads").EnumerateArray())
            .Select(p => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p.GetProperty("data").GetString()!)))
            .ToList();

        Assert.Contains(patches, p => p.Contains($"\"{marker}\"", StringComparison.Ordinal));
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
