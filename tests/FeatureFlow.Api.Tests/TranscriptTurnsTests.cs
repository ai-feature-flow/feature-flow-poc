using System.Net;
using System.Text.Json.Nodes;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;
using static FeatureFlow.Api.Contracts.TranscriptTurnKind;
using Kind = FeatureFlow.Api.Contracts.TranscriptTurnKind;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// An agent session's transcript read as turns (console A4), one shape for both runtimes: a real Copilot CLI transcript
/// (run-947be4435f5c's plan.plan, execution 1, with all but a few of its streamed tool arguments and background-task events
/// left out) and Claude Code's stream-json as version 2.1 prints it.
/// </summary>
public sealed class TranscriptTurnsTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Api.Tests", "data", name));

    static readonly string Copilot = Fixture("copilot-transcript.jsonl");
    static readonly string Claude = Fixture("claude-transcript.jsonl");

    static TranscriptTool Tool(TranscriptTurn turn) => turn.Tool!;

    [Fact]
    public void A_copilot_transcript_is_its_prompt_messages_reasoning_tool_calls_and_result()
    {
        var read = Transcripts.Read(Copilot)!;

        Assert.Equal((TranscriptRuntime.CopilotCli, "claude-sonnet-5.5"), (read.Runtime, read.Model));
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T03:03:40.581Z", System.Globalization.CultureInfo.InvariantCulture), read.Started);
        Assert.Equal((26845L, 7, 7, 100462L, 3130L), (read.DurationMs, read.ToolCalls, read.ModelCalls, read.InputTokens, read.OutputTokens));
        Assert.Equal([Prompt, Kind.Tool, Kind.Tool, Kind.Tool, Message, Kind.Tool, Kind.Tool, Reasoning, Kind.Tool, Reasoning, Kind.Tool, Message, Result], read.Turns.Select(t => t.Kind));
        Assert.Empty(read.Unread);

        var turns = read.Turns;
        Assert.StartsWith("# How this step works", turns[0].Text);
        Assert.Equal(("bash", "Read inputs"), (Tool(turns[1]).Name, Tool(turns[1]).Summary));
        Assert.Contains("\"command\": \"cat /ff/inputs/manifest.json; cat /ff/inputs/ticket.md; ls /ff/inputs /ff/work\"", Tool(turns[1]).Arguments);
        Assert.Contains("\"step\": \"plan.plan\"", Tool(turns[1]).Output);
        // A message streamed in pieces is one turn, where its first piece arrived.
        Assert.Equal("Unit tier has parsed results. Writing documents.", turns[4].Text);
        // The shell write that was denied failed; the report the engine rejected is a result, not a failure.
        Assert.Equal(("bash", true), (Tool(turns[5]).Name, turns[5].Failed));
        Assert.StartsWith("Permission denied because no interactive user response was available.", Tool(turns[5]).Output);
        Assert.Equal(("engine-report", false), (Tool(turns[6]).Name, turns[6].Failed));
        Assert.StartsWith("Report rejected:", Tool(turns[6]).Output);
        Assert.StartsWith("I can't use bash to write file contents", turns[7].Text);
        Assert.Equal([false, false, false, true, false, true, false], turns.Where(t => t.Kind == Kind.Tool).Select(t => t.Failed));
        Assert.StartsWith("I couldn't write `requirements.yaml`", turns[11].Text);
        Assert.Equal((null, false), (turns[12].Text, turns[12].Failed));
        Assert.All(turns, t => Assert.NotNull(t.At));
    }

    [Fact]
    public void A_cut_transcript_keeps_its_streamed_message_and_counts_unknown_events_and_unreadable_lines()
    {
        // Cut before the last message's final event: only its pieces remain. Then an event type this does not know, and a line cut short.
        var lines = Copilot.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var final = Array.FindLastIndex(lines, l => l.StartsWith("{\"type\":\"assistant.message\",", StringComparison.Ordinal));
        var pieces = string.Concat(lines[..final].Select(l => JsonNode.Parse(l)!).Where(e => (string?)e["type"] == "assistant.message_delta"
            && (string?)e["data"]!["messageId"] == "ef6d843d-3e22-44ae-acbd-02a8c469fc36").Select(e => (string?)e["data"]!["deltaContent"]));
        var cut = string.Join('\n', lines[..final]) + "\n{\"type\":\"session.future_thing\",\"data\":{}}\n{\"type\":\"assistant.message\",\"data\":{\"mess";

        var read = Transcripts.Read(cut)!;

        Assert.StartsWith("I couldn't write `requirements.yaml`", pieces);
        Assert.Equal((Message, pieces), (read.Turns[^1].Kind, read.Turns[^1].Text));
        Assert.Equal([new UnreadEvents("session.future_thing", 1), new UnreadEvents("(unreadable line)", 1)], read.Unread);
        // Without the result event, the duration is the span of the events' times.
        Assert.InRange(read.DurationMs!.Value, 26_000, 27_000);
    }

    [Fact]
    public void A_claude_code_transcript_reads_in_the_same_shape()
    {
        var read = Transcripts.Read(Claude + "{\"type\":\"rate_limit_event\"}\nnot json\n")!;

        Assert.Equal((TranscriptRuntime.ClaudeCode, "claude-haiku-4-5-20251001", (DateTimeOffset?)null), (read.Runtime, read.Model, read.Started));
        Assert.Equal((14250L, 3, 4, 13640L, 310L), (read.DurationMs, read.ToolCalls, read.ModelCalls, read.InputTokens, read.OutputTokens));
        Assert.Equal([Reasoning, Kind.Tool, Message, Kind.Tool, Kind.Tool, Message, Result], read.Turns.Select(t => t.Kind));
        var turns = read.Turns;
        Assert.Equal("I should read the inputs manifest first.", turns[0].Text);
        Assert.Equal(("Bash", "Read the inputs manifest", false), (Tool(turns[1]).Name, Tool(turns[1]).Summary, turns[1].Failed));
        Assert.Equal("{\n  \"step\": \"build\",\n  \"output_folder\": \"/ff/out\"\n}", Tool(turns[1]).Output);
        // The streamed pieces are dropped: the assistant record carries the whole message.
        Assert.Equal("The manifest names the step **build**. Reading the ticket.", turns[2].Text);
        Assert.Equal(("Read", true, "<tool_use_error>File does not exist.</tool_use_error>"), (Tool(turns[3]).Name, turns[3].Failed, Tool(turns[3]).Output));
        Assert.Equal("Report accepted. Your step is finished: stop now.", Tool(turns[4]).Output);
        Assert.Contains("\"outcome\": \"done\"", Tool(turns[4]).Arguments);
        Assert.Equal(("Done: the step is reported.", false), (turns[6].Text, turns[6].Failed));
        Assert.Equal([new UnreadEvents("rate_limit_event", 1), new UnreadEvents("(unreadable line)", 1)], read.Unread);
    }

    [Fact]
    public void Text_that_holds_no_runtimes_events_is_not_a_transcript()
    {
        Assert.Null(Transcripts.Read("# Plan\n\n**bold**\n"));
        Assert.Null(Transcripts.Read("{\"schema\": 1}\n"));
    }

    [Fact]
    public async Task The_route_reads_a_runs_transcript_as_turns_and_refuses_text_that_is_not_one()
    {
        await using var world = new DecisionWorld();
        var run = await world.StartAsync();
        var at = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
        var transcript = await world.PutAsync(Claude);
        var text = await world.PutAsync("agent: done");
        await world.AppendAsync(new RecordBatch(Artifacts: [
            new ArtifactRecord(run.RunId, "build", 1, "transcript:1", "transcript", transcript, null, false, at),
            new ArtifactRecord(run.RunId, "build", 1, "transcript:2", "transcript", text, null, false, at)]));

        var turns = await world.GetAsync<TranscriptTurns>($"runs/{run.RunId}/artifacts/{transcript}/turns");
        using var refused = await world.Http.GetAsync($"runs/{run.RunId}/artifacts/{text}/turns", DecisionWorld.Ct);
        using var missing = await world.Http.GetAsync($"runs/{run.RunId}/artifacts/{await world.PutAsync(Claude + " ")}/turns", DecisionWorld.Ct);

        Assert.Equal((TranscriptRuntime.ClaudeCode, 7), (turns.Runtime, turns.Turns.Count));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
