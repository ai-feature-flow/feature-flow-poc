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
        Assert.Equal(["agent_question", "epic_delivery", "epic_delivery_child", "sp1_parent", "sp1_parent_resumed", "ticket_delivery"],
            Names());
    }

    [Theory]
    [MemberData(nameof(Histories))]
    public async Task A_recorded_history_replays_against_the_current_workflow(string name)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(HistoryFolder, name + ".json"), TestContext.Current.CancellationToken);
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<RunWorkflow>());

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
