using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>
/// The runtimes' shared reader fed a session's output as it grows (live activity): the entries each runtime's completed
/// events make, whatever the chunks; numbered the same on a second read; capped; a failed tool call marked; deltas no entry.
/// </summary>
public sealed class AgentTranscriptsTests
{
    static string Lines(params JsonObject[] events) => string.Concat(events.Select(e => e.ToJsonString() + "\n"));

    static readonly string Long = string.Concat(Enumerable.Range(0, 1_000).Select(i => $"{i:D4}|")); // 5,000 characters, each place readable

    /// <summary>Copilot CLI: a streamed message, reasoning, two shell calls (the second failing by exit code), a warning, an error.</summary>
    static string Copilot(string message = "Reading the ticket.", string output = "one\n") => Lines(
        new JsonObject { ["type"] = "user.message", ["data"] = new JsonObject { ["content"] = "the task" } },
        new JsonObject { ["type"] = "assistant.message_delta", ["data"] = new JsonObject { ["messageId"] = "m1", ["deltaContent"] = "Reading" } },
        new JsonObject { ["type"] = "assistant.message_delta", ["data"] = new JsonObject { ["messageId"] = "m1", ["deltaContent"] = " the" } },
        new JsonObject
        {
            ["type"] = "assistant.message", ["timestamp"] = "2026-11-24T01:00:00Z",
            ["data"] = new JsonObject
            {
                ["messageId"] = "m1", ["content"] = message,
                ["toolRequests"] = new JsonArray(new JsonObject { ["toolCallId"] = "c1", ["intentionSummary"] = "Say one" }),
            },
        },
        new JsonObject { ["type"] = "assistant.reasoning", ["data"] = new JsonObject { ["reasoningId"] = "r1", ["content"] = "Echo first." } },
        new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolCallId"] = "c1", ["toolName"] = "bash", ["arguments"] = new JsonObject { ["command"] = "echo one" } } },
        new JsonObject { ["type"] = "tool.shell_output", ["data"] = new JsonObject { ["toolCallId"] = "c1", ["text"] = output } },
        new JsonObject { ["type"] = "tool.execution_complete", ["data"] = new JsonObject { ["toolCallId"] = "c1", ["success"] = true } },
        new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolCallId"] = "c2", ["toolName"] = "bash", ["arguments"] = new JsonObject { ["command"] = "false" } } },
        new JsonObject
        {
            ["type"] = "tool.execution_complete",
            ["data"] = new JsonObject { ["toolCallId"] = "c2", ["success"] = true, ["result"] = new JsonObject { ["content"] = "" }, ["shellExecution"] = new JsonObject { ["exitCode"] = 1 } },
        },
        new JsonObject { ["type"] = "session.warning", ["data"] = new JsonObject { ["message"] = "rate limited" } },
        new JsonObject { ["type"] = "session.error", ["data"] = new JsonObject { ["message"] = "model unreachable" } },
        new JsonObject { ["type"] = "result", ["exitCode"] = 1 });

    /// <summary>Claude Code: a partial, thinking, a message, a shell call and a failed read.</summary>
    static string Claude(string message = "Reading the ticket.", string output = "one\n") => Lines(
        new JsonObject { ["type"] = "system", ["model"] = "claude-haiku-4-5" },
        new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["delta"] = new JsonObject { ["text"] = "Read" } } },
        new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["id"] = "a1", ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "Echo first." }) } },
        new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["id"] = "a1", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }) } },
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["id"] = "a1",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = "t1", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "echo one", ["description"] = "Say one" },
                }),
            },
        },
        new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = output }) } },
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["id"] = "a2",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "t2", ["name"] = "Read", ["input"] = new JsonObject { ["file_path"] = "/ff/none" } }),
            },
        },
        new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t2", ["content"] = "missing", ["is_error"] = true }) },
        },
        new JsonObject { ["type"] = "result", ["result"] = "Done." });

    static IReadOnlyList<ActivityEntry> FeedWhole(TranscriptFormat format, string output) => AgentTranscripts.Reader(format).Feed(output);

    [Fact]
    public void Copilots_completed_events_are_its_entries_and_deltas_none()
    {
        var entries = FeedWhole(TranscriptFormat.CopilotCli, Copilot());

        Assert.Equal([
            (1, ActivityKinds.Message, null, null, "Reading the ticket.", null, false),
            (2, ActivityKinds.Reasoning, null, null, "Echo first.", null, false),
            (3, ActivityKinds.ToolStarted, "c1", "bash", "Say one", "{\n  \"command\": \"echo one\"\n}", false),
            (4, ActivityKinds.ToolFinished, "c1", "bash", null, "one\n", false),
            (5, ActivityKinds.ToolStarted, "c2", "bash", null, "{\n  \"command\": \"false\"\n}", false),
            (6, ActivityKinds.ToolFinished, "c2", "bash", null, "", true),
            (7, ActivityKinds.Notice, null, null, "rate limited", null, false),
            (8, ActivityKinds.Error, null, null, "model unreachable", null, true),
        ], entries.Select(e => (e.Sequence, e.Kind, e.CallId, e.Name, e.Text, e.Detail, e.Failed)));
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture), entries[0].At);
    }

    [Fact]
    public void Claude_codes_completed_blocks_are_its_entries_and_partials_none()
    {
        var entries = FeedWhole(TranscriptFormat.ClaudeCode, Claude());

        Assert.Equal([
            (1, ActivityKinds.Reasoning, null, null, "Echo first.", null, false),
            (2, ActivityKinds.Message, null, null, "Reading the ticket.", null, false),
            (3, ActivityKinds.ToolStarted, "t1", "Bash", "Say one", "{\n  \"command\": \"echo one\",\n  \"description\": \"Say one\"\n}", false),
            (4, ActivityKinds.ToolFinished, "t1", "Bash", null, "one\n", false),
            (5, ActivityKinds.ToolStarted, "t2", "Read", null, "{\n  \"file_path\": \"/ff/none\"\n}", false),
            (6, ActivityKinds.ToolFinished, "t2", "Read", null, "missing", true),
        ], entries.Select(e => (e.Sequence, e.Kind, e.CallId, e.Name, e.Text, e.Detail, e.Failed)));
    }

    public static TheoryData<TranscriptFormat> Formats => [TranscriptFormat.CopilotCli, TranscriptFormat.ClaudeCode];

    static string Output(TranscriptFormat format, string message = "Reading the ticket.", string output = "one\n") =>
        format == TranscriptFormat.CopilotCli ? Copilot(message, output) : Claude(message, output);

    /// <summary>The CLI's output arrives in chunks that end anywhere, mid-line included: the entries are the same, each once.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void Output_fed_in_chunks_split_mid_line_gives_the_same_entries(TranscriptFormat format)
    {
        var output = Output(format);
        var reader = AgentTranscripts.Reader(format);

        var chunked = output.Chunk(7).Select(c => reader.Feed(new string(c))).SelectMany(e => e).ToList();

        Assert.Equal(FeedWhole(format, output), chunked);
        // A line not yet ended is kept, not read: it makes its entry when its end arrives.
        var partial = AgentTranscripts.Reader(format);
        var cut = output.LastIndexOf(format == TranscriptFormat.CopilotCli ? "session.error" : "\"tool_result\"", StringComparison.Ordinal);
        var before = partial.Feed(output[..cut]);
        Assert.Equal(FeedWhole(format, output).Count - 1, before.Count);
        Assert.Single(partial.Feed(output[cut..]));
    }

    /// <summary>A new worker re-attaching reads the output from the start: its entries are numbered as the first read numbered them.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_second_read_of_the_same_output_numbers_its_entries_the_same(TranscriptFormat format)
    {
        var output = Output(format);
        var first = AgentTranscripts.Reader(format);
        var firstRead = first.Feed(output[..(output.Length / 2)]).Concat(first.Feed(output[(output.Length / 2)..])).ToList();

        Assert.Equal(firstRead, FeedWhole(format, output));
        Assert.Equal(Enumerable.Range(1, firstRead.Count), firstRead.Select(e => e.Sequence));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Entries_are_capped_a_message_by_its_head_and_tool_output_by_its_tail(TranscriptFormat format)
    {
        var entries = FeedWhole(format, Output(format, Long, Long));

        var message = entries.Single(e => e.Kind == ActivityKinds.Message).Text!;
        var output = entries.First(e => e.Kind == ActivityKinds.ToolFinished).Detail!;
        Assert.Equal(Long[..AgentTranscripts.MessageCap] + "…", message);
        Assert.Equal("…" + Long[^AgentTranscripts.OutputCap..], output);
    }

    [Fact]
    public void Reasoning_a_summary_arguments_and_a_notice_are_capped_by_their_heads()
    {
        var output = Lines(
            new JsonObject { ["type"] = "assistant.reasoning", ["data"] = new JsonObject { ["reasoningId"] = "r1", ["content"] = Long } },
            new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolCallId"] = "c1", ["toolName"] = "bash", ["arguments"] = new JsonObject { ["description"] = Long } } },
            new JsonObject { ["type"] = "session.warning", ["data"] = new JsonObject { ["message"] = Long } });

        var entries = FeedWhole(TranscriptFormat.CopilotCli, output);

        Assert.Equal(Long[..AgentTranscripts.ReasoningCap] + "…", entries[0].Text);
        Assert.Equal(Long[..AgentTranscripts.SummaryCap] + "…", entries[1].Text);
        Assert.Equal(AgentTranscripts.ArgumentsCap + 1, entries[1].Detail!.Length);
        Assert.Equal(Long[..AgentTranscripts.NoticeCap] + "…", entries[2].Text);
    }

    /// <summary>The whole-text read and the fed reader are one reader: the turns built while feeding are the transcript's.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void The_turns_built_while_feeding_are_the_whole_texts(TranscriptFormat format)
    {
        var output = Output(format);
        var reader = AgentTranscripts.Reader(format);
        foreach (var chunk in output.Chunk(11))
            reader.Feed(new string(chunk));

        var whole = AgentTranscripts.Read(output)!;
        var fed = reader.Turns();
        Assert.Equal(format, whole.Format);
        Assert.Equal(whole.Turns, fed.Turns);
        Assert.Equal((whole.ToolCalls, whole.ModelCalls), (fed.ToolCalls, fed.ModelCalls));
    }

    /// <summary>The agent worker's live reader makes the same entries, capped the same, but keeps no turns: its memory does not grow with the session.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void A_live_reader_makes_the_same_entries_and_keeps_no_turns(TranscriptFormat format)
    {
        var output = Output(format, Long, Long);
        var live = AgentTranscripts.Reader(format, live: true);

        var entries = output.Chunk(11).SelectMany(c => live.Feed(new string(c))).ToList();

        Assert.Equal(FeedWhole(format, output), entries);
        Assert.Empty(live.Turns().Turns);
    }
}
