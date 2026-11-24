using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;
using static FeatureFlow.Api.Contracts.TranscriptTurnKind;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>GET /runs/{id}/steps/{step}/activity</c>: an agent step's sessions as turns, from the activity recorded while each runs
/// (<see cref="ActivityTurns"/>), or its stored transcript once indexed; the latest agent execution unless one is named.
/// </summary>
public sealed class ActivityTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    static ActivityRecord Entry(int sequence, string kind, string? callId = null, string? name = null, string? text = null, string? detail = null, bool failed = false,
        string run = "r", string step = "build", int execution = 1, int session = 1) =>
        new(run, step, execution, session, sequence, kind, callId, name, text, detail, failed, T0.AddSeconds(sequence));

    [Fact]
    public void Records_read_as_turns_merge_a_tool_calls_start_and_finish_and_leave_a_running_call_without_output()
    {
        var turns = ActivityTurns.Of(
        [
            Entry(7, ActivityKinds.ToolStarted, "c2", "bash", "Run tests", "{\"command\": \"make test\"}"),
            Entry(1, ActivityKinds.Message, text: "Looking"),
            Entry(2, ActivityKinds.Reasoning, text: "think"),
            Entry(3, ActivityKinds.ToolStarted, "c1", "bash", "List", "{\"command\": \"ls\"}"),
            Entry(4, ActivityKinds.Notice, text: "slow"),
            Entry(5, ActivityKinds.ToolFinished, "c1", detail: "a\nb", failed: true),
            Entry(6, ActivityKinds.ToolFinished, "c0", "view", detail: null),
            Entry(8, ActivityKinds.Error, text: "boom"),
            Entry(int.MaxValue, ActivityKinds.Truncated),
        ]);

        Assert.Equal([Message, Reasoning, Tool, Notice, Tool, Tool, Error, Notice], turns.Select(t => t.Kind));
        Assert.Equal(("Looking", T0.AddSeconds(1)), (turns[0].Text, turns[0].At));
        // Started at 3, finished at 5: one turn, where it started, failed, with the output's tail.
        Assert.Equal((new TranscriptTool("bash", "List", "{\"command\": \"ls\"}", "a\nb"), true, T0.AddSeconds(3)), (turns[2].Tool, turns[2].Failed, turns[2].At));
        // A finish with no start is a turn of its own; a finished call's output is never null.
        Assert.Equal((new TranscriptTool("view", null, null, ""), false), (turns[4].Tool, turns[4].Failed));
        // Still running: no output yet.
        Assert.Equal(new TranscriptTool("bash", "Run tests", "{\"command\": \"make test\"}", null), turns[5].Tool);
        Assert.Equal(("boom", true), (turns[6].Text, turns[6].Failed));
        Assert.Equal(ActivityTurns.TruncatedNotice, turns[7].Text);
    }

    /// <summary>An unnamed call is <c>tool</c> live, as it is once the transcript is read.</summary>
    [Fact]
    public void A_tool_call_with_no_name_is_named_tool_as_in_a_transcript()
    {
        var turns = ActivityTurns.Of([Entry(1, ActivityKinds.ToolStarted, "c1"), Entry(2, ActivityKinds.ToolFinished, "c2", detail: "x")]);

        Assert.Equal(["tool", "tool"], turns.Select(t => t.Tool!.Name));
    }

    static async Task<string> PromptAsync(ApiWorld world, string run, string step, int execution, int session) =>
        await IndexAsync(world, run, step, execution, ArtifactNames.Prompt(session), ArtifactNames.PromptType, "the task");

    static async Task<string> IndexAsync(ApiWorld world, string run, string step, int execution, string name, string? type, string text)
    {
        var reference = await world.StoreAsync(text);
        await world.Records.AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord(run, step, execution, name, type, reference, null, false, T0)]), Ct);
        return reference;
    }

    static async Task<(HttpStatusCode Status, StepActivity? Body, string Text)> GetAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonSerializer.Deserialize<StepActivity>(text, ApiJson.Options) : null, text);
    }

    [Fact]
    public async Task A_running_session_is_read_live_from_its_records_and_a_stored_transcript_is_preferred_once_indexed()
    {
        await using var world = new ApiWorld();
        await world.RunAsync("r");
        await PromptAsync(world, "r", "build", 1, 1);
        await PromptAsync(world, "r", "build", 1, 2);
        await world.Records.AppendAsync(new RecordBatch(Activity:
        [
            Entry(1, ActivityKinds.Message, text: "first session", session: 1),
            Entry(1, ActivityKinds.Message, text: "Looking", session: 2),
            Entry(2, ActivityKinds.ToolStarted, "c1", "bash", "List", "{}", session: 2),
            Entry(int.MaxValue, ActivityKinds.Truncated, session: 2),
            Entry(1, ActivityKinds.Message, text: "another step", step: "plan"),
        ]), Ct);
        var viewer = world.As("vic", "viewer");

        var (status, live, _) = await GetAsync(viewer, "runs/r/steps/build/activity");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, live!.Execution);
        Assert.Equal([1], live.Executions);
        Assert.Equal([(1, "live", (string?)null, false), (2, "live", null, true)], live.Sessions.Select(s => (s.Session, s.Source, s.TranscriptRef, s.Truncated)));
        Assert.Equal(["first session"], live.Sessions[0].Turns.Select(t => t.Text));
        Assert.Equal([Message, Tool, Notice], live.Sessions[1].Turns.Select(t => t.Kind));
        Assert.Null(live.Sessions[1].Turns[1].Tool!.Output);

        var transcript = await IndexAsync(world, "r", "build", 1, ArtifactNames.Transcript(1), ArtifactNames.TranscriptType,
            File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Api.Tests", "data", "copilot-transcript.jsonl")));

        var (_, stored, _) = await GetAsync(viewer, "runs/r/steps/build/activity");

        Assert.Equal([(1, "transcript", (string?)transcript), (2, "live", null)], stored!.Sessions.Select(s => (s.Session, s.Source, s.TranscriptRef)));
        Assert.Equal(Transcripts.Read(File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Api.Tests", "data", "copilot-transcript.jsonl")))!.Turns,
            stored.Sessions[0].Turns);
    }

    [Fact]
    public async Task The_latest_agent_execution_is_shown_unless_one_is_named_and_executions_without_a_prompt_are_not_listed()
    {
        await using var world = new ApiWorld();
        await world.RunAsync("r");
        await PromptAsync(world, "r", "build", 1, 1);
        await IndexAsync(world, "r", "build", 2, "report", null, "not an agent execution");
        await PromptAsync(world, "r", "build", 3, 1);
        await world.Records.AppendAsync(new RecordBatch(Activity:
            [Entry(1, ActivityKinds.Message, text: "one", execution: 1), Entry(1, ActivityKinds.Message, text: "three", execution: 3)]), Ct);
        var viewer = world.As("vic", "viewer");

        var (_, latest, _) = await GetAsync(viewer, "runs/r/steps/build/activity");
        var (_, first, _) = await GetAsync(viewer, "runs/r/steps/build/activity?execution=1");
        var (notAgent, _, _) = await GetAsync(viewer, "runs/r/steps/build/activity?execution=2");

        Assert.Equal((3, "three"), (latest!.Execution, latest.Sessions.Single().Turns.Single().Text));
        Assert.Equal([1, 3], latest.Executions);
        Assert.Equal((1, "one"), (first!.Execution, first.Sessions.Single().Turns.Single().Text));
        Assert.Equal(HttpStatusCode.NotFound, notAgent);
    }

    [Fact]
    public async Task An_unknown_run_or_step_is_404_and_a_step_with_no_agent_execution_yet_has_no_sessions()
    {
        await using var world = new ApiWorld();
        await world.RunAsync("r");
        await world.Records.AppendAsync(new RecordBatch(Timeline: [new TimelineRecord("r", 1, "started", "build", "execution 1", T0)]), Ct);
        var viewer = world.As("vic", "viewer");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, "runs/nope/steps/build/activity")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(viewer, "runs/r/steps/nope/activity")).Status);
        var (status, started, _) = await GetAsync(viewer, "runs/r/steps/build/activity");
        Assert.Equal((HttpStatusCode.OK, 0, 0, 0), (status, started!.Execution, started.Executions.Count, started.Sessions.Count));
    }

    [Fact]
    public async Task Only_a_viewer_of_the_run_reads_its_activity()
    {
        await using var world = new ApiWorld(EventsTests.TwoTeams());
        await world.RunAsync("r", "acme/pay");
        await PromptAsync(world, "r", "build", 1, 1);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(world.As("pat", groups: "pay-eng"), "runs/r/steps/build/activity")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(world.As("wes", groups: "web-eng"), "runs/r/steps/build/activity")).Status);
    }
}
