using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>The runtime whose output a transcript is: Copilot CLI's JSON-lines session events or Claude Code's stream-json.</summary>
public enum TranscriptFormat
{
    CopilotCli,
    ClaudeCode,
}

/// <summary>What a turn is: the prompt, a message from the agent, its reasoning, a tool call, a notice or error from the runtime, or the session's result.</summary>
public enum AgentTurnKind
{
    Prompt,
    Message,
    Reasoning,
    Tool,
    Notice,
    Error,
    Result,
}

/// <summary>A tool call: the tool's name, the agent's summary of it, its arguments as indented JSON text, and its result or output as text.</summary>
public sealed record AgentTool(string Name, string? Summary, string? Arguments, string? Output);

/// <summary>One turn: its kind, when it happened (null where the runtime records no times), its text, the tool call for a tool turn, and whether it failed.</summary>
public sealed record AgentTurn(AgentTurnKind Kind, DateTimeOffset? At, string? Text, AgentTool? Tool, bool Failed);

/// <summary>An event type a transcript holds that is not shown, and how many; unreadable lines are counted as <c>(unreadable line)</c>.</summary>
public sealed record AgentUnread(string Type, int Count);

/// <summary>
/// A session's transcript as turns: the runtime, the model, when it started, how long it took, its tool and model calls, the
/// tokens it used (input counting cached input), its turns in order, and the event types not shown.
/// </summary>
public sealed record AgentTurns(
    TranscriptFormat Format,
    string? Model,
    DateTimeOffset? Started,
    long? DurationMs,
    int ToolCalls,
    int ModelCalls,
    long? InputTokens,
    long? OutputTokens,
    IReadOnlyList<AgentTurn> Turns,
    IReadOnlyList<AgentUnread> Unread);

/// <summary>
/// One completed event of a running session (an <see cref="ActivityKinds"/> kind), capped (<see cref="AgentTranscripts"/>):
/// <see cref="Sequence"/> is its place among the session's entries from the start of its output, so reading the output
/// again numbers it the same. <see cref="Text"/> is a message, reasoning, notice or a tool call's summary; <see cref="Detail"/>
/// a tool call's arguments or the tail of its output.
/// </summary>
public sealed record ActivityEntry(int Sequence, string Kind, string? CallId, string? Name, string? Text, string? Detail, bool Failed, DateTimeOffset? At);

/// <summary>
/// An agent session's output read as turns and as activity entries (console A4, live activity), for both runtimes. Streaming
/// deltas are merged into the turn they build, or dropped where a final event carries the whole, and never make an entry:
/// a message is an entry once complete. Event types read for nothing are passed over; any other, and any line that is not
/// a JSON object, is counted as unread, never fatal. Turns and entries come from the same code for each event, so the two
/// views cannot drift.
/// </summary>
public static class AgentTranscripts
{
    /// <summary>The longest an entry's text or detail is kept, by what it holds (characters; output keeps its tail, the rest their head).</summary>
    public const int MessageCap = 4_000, ReasoningCap = 1_000, SummaryCap = 300, ArgumentsCap = 2_000, OutputCap = 2_000, NoticeCap = 1_000;

    /// <summary>The transcript's turns, or null when no line is an event of a runtime this reads.</summary>
    public static AgentTurns? Read(string text)
    {
        var records = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(TranscriptReader.ObjectOf).ToList();
        // Copilot's event types are dotted (user.message); Claude Code's are single words.
        TranscriptFormat? format = records.Any(r => TranscriptReader.TypeOf(r)?.Contains('.', StringComparison.Ordinal) == true) ? TranscriptFormat.CopilotCli
            : records.Any(r => TranscriptReader.TypeOf(r) is "system" or "assistant" or "user") ? TranscriptFormat.ClaudeCode
            : null;
        if (format is not { } known)
            return null;
        var reader = Reader(known);
        foreach (var record in records)
            reader.Take(record);
        return reader.Turns();
    }

    /// <summary>
    /// An incremental reader of a session's output in <paramref name="format"/>, fed as the output grows. A <paramref name="live"/>
    /// reader is read for its entries only: it keeps no turns, forgets a call or message once its entry is made, and keeps
    /// only the tail of a shell's output, so what it holds does not grow with the session.
    /// </summary>
    public static TranscriptReader Reader(TranscriptFormat format, bool live = false) => format switch
    {
        TranscriptFormat.CopilotCli => new CopilotReader { Live = live },
        _ => new ClaudeReader { Live = live },
    };
}

/// <summary>
/// Reads a session's output as it grows (<see cref="Feed"/>): each complete line is read, a partial last line is kept for
/// the next chunk, and the activity entries the lines completed are returned. <see cref="Turns"/> is everything read so far.
/// </summary>
public abstract class TranscriptReader
{
    readonly StringBuilder partial = new();
    readonly List<Turn> turns = [];
    readonly Dictionary<string, Turn> byId = [];
    readonly Dictionary<string, int> unread = [];
    readonly List<ActivityEntry> entries = [];
    int sequence;
    protected string? Model;
    protected DateTimeOffset? Started, Last;
    protected long? DurationMs, InputTokens, OutputTokens;
    protected int ToolCalls, ModelCalls;

    private protected TranscriptReader()
    {
    }

    public abstract TranscriptFormat Format { get; }

    /// <summary>Read for entries only (<see cref="AgentTranscripts.Reader"/>): <see cref="Turns"/> then holds no turns.</summary>
    internal bool Live { get; init; }

    /// <summary>Reads the complete lines <paramref name="chunk"/> ends, keeping a partial last line, and returns the entries they completed.</summary>
    public IReadOnlyList<ActivityEntry> Feed(string chunk)
    {
        partial.Append(chunk);
        var text = partial.ToString();
        var end = text.LastIndexOf('\n');
        if (end < 0)
            return [];
        partial.Remove(0, end + 1);
        foreach (var line in text[..end].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            Take(ObjectOf(line));
        var completed = entries.ToList();
        entries.Clear();
        return completed;
    }

    internal void Take(JsonObject? record)
    {
        if (record is null)
            Unread("(unreadable line)");
        else
            Read(record);
    }

    protected abstract void Read(JsonObject record);

    protected void Unread(string type) => unread[type] = unread.GetValueOrDefault(type) + 1;

    public AgentTurns Turns() => new(Format, Model, Started, DurationMs ?? (long?)(Last - Started)?.TotalMilliseconds, ToolCalls, ModelCalls, InputTokens, OutputTokens,
        turns.Select(t => t.ToTurn()).ToList(), unread.Select(u => new AgentUnread(u.Key, u.Value)).ToList());

    internal static JsonObject? ObjectOf(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? TypeOf(JsonObject? record) => Text(record?["type"]);

    protected static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    protected static long? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    protected static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A tool's arguments as indented JSON text; shown as text, so nothing in them needs escaping beyond JSON's own.</summary>
    protected static string? Pretty(JsonNode? node) => node?.ToJsonString(Indented);

    protected sealed class Turn(AgentTurnKind kind, DateTimeOffset? at, string? text)
    {
        public AgentTurnKind Kind { get; } = kind;
        public DateTimeOffset? At { get; } = at;
        public StringBuilder Text { get; } = new(text);
        public string? Name { get; init; }
        public string? Summary { get; set; }
        public string? Arguments { get; init; }
        public string? Output { get; set; }
        public StringBuilder Shell { get; } = new();
        public bool Failed { get; set; }

        public string? ToolOutput => Output ?? (Shell.Length > 0 ? Shell.ToString() : null);

        public AgentTurn ToTurn() => new(Kind, At, Kind == AgentTurnKind.Tool || Text.Length == 0 ? null : Text.ToString(),
            Kind == AgentTurnKind.Tool ? new AgentTool(Name ?? "tool", Summary, Arguments, ToolOutput) : null, Failed);
    }

    protected Turn Add(AgentTurnKind kind, DateTimeOffset? at, string? text, string? id = null)
    {
        var turn = new Turn(kind, at, text);
        if (!Live)
            turns.Add(turn);
        if (id is not null)
            byId[id] = turn;
        return turn;
    }

    protected Turn Add(Turn turn, string id)
    {
        if (!Live)
            turns.Add(turn);
        byId[id] = turn;
        return turn;
    }

    protected Turn? Find(string? id) => id is not null && byId.TryGetValue(id, out var turn) ? turn : null;

    /// <summary>A streamed piece of the turn <paramref name="id"/> names: the turn starts where its first piece arrives. No entry: the whole follows.</summary>
    protected void Append(AgentTurnKind kind, string? id, DateTimeOffset? at, string? piece)
    {
        if (id is null)
            return;
        (Find(kind + id) ?? Add(kind, at, null, kind + id)).Text.Append(piece);
    }

    /// <summary>The whole of the turn <paramref name="id"/> names: it replaces the pieces streamed, keeping their place, and is an entry.</summary>
    protected void Complete(AgentTurnKind kind, string? id, DateTimeOffset? at, string? whole)
    {
        if (string.IsNullOrEmpty(whole))
            return;
        if (id is not null && Find(kind + id) is { } streamed)
            streamed.Text.Clear().Append(whole);
        else
            Add(kind, at, whole, id is null ? null : kind + id);
        if (Live && id is not null)
            byId.Remove(kind + id);
        Said(kind, at, whole);
    }

    /// <summary>A message, reasoning, notice or error as an entry; nothing for empty text.</summary>
    protected void Said(AgentTurnKind kind, DateTimeOffset? at, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var (entryKind, cap) = kind switch
        {
            AgentTurnKind.Message => (ActivityKinds.Message, AgentTranscripts.MessageCap),
            AgentTurnKind.Reasoning => (ActivityKinds.Reasoning, AgentTranscripts.ReasoningCap),
            AgentTurnKind.Notice => (ActivityKinds.Notice, AgentTranscripts.NoticeCap),
            AgentTurnKind.Error => (ActivityKinds.Error, AgentTranscripts.NoticeCap),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an entry"),
        };
        Entry(entryKind, at, null, null, Head(text, cap), null, kind == AgentTurnKind.Error);
    }

    /// <summary>A tool call starting, as a turn and an entry.</summary>
    protected Turn ToolStarted(Turn tool, string id)
    {
        ToolCalls++;
        Add(tool, id);
        Entry(ActivityKinds.ToolStarted, tool.At, id, tool.Name, Head(tool.Summary, AgentTranscripts.SummaryCap), Head(tool.Arguments, AgentTranscripts.ArgumentsCap), false);
        return tool;
    }

    /// <summary>A tool call finishing, once its output and failure are set on its turn, as an entry.</summary>
    protected void ToolFinished(Turn tool, string? id, DateTimeOffset? at)
    {
        Entry(ActivityKinds.ToolFinished, at, id, tool.Name, null, Tail(tool.ToolOutput, AgentTranscripts.OutputCap), tool.Failed);
        if (Live && id is not null)
            byId.Remove(id);
    }

    /// <summary>A piece of a running shell's output; a live reader keeps only as much of its tail as an entry shows.</summary>
    protected void ShellOutput(Turn? tool, string? piece)
    {
        if (tool is null)
            return;
        tool.Shell.Append(piece);
        if (Live && tool.Shell.Length > AgentTranscripts.OutputCap + 1)
            tool.Shell.Remove(0, tool.Shell.Length - AgentTranscripts.OutputCap - 1);
    }

    void Entry(string kind, DateTimeOffset? at, string? callId, string? name, string? text, string? detail, bool failed) =>
        entries.Add(new ActivityEntry(++sequence, kind, callId, name, text, detail, failed, at));

    static string? Head(string? text, int cap) => text is { } t && t.Length > cap ? t[..cap] + "…" : text;

    static string? Tail(string? text, int cap) => text is { } t && t.Length > cap ? "…" + t[^cap..] : text;

    protected DateTimeOffset? At(JsonObject record)
    {
        var at = DateTimeOffset.TryParse(Text(record["timestamp"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : (DateTimeOffset?)null;
        Started ??= at;
        Last = at ?? Last;
        return at;
    }
}

/// <summary>Copilot CLI's JSON-lines session events (<c>copilot --output-format json</c>).</summary>
sealed class CopilotReader : TranscriptReader
{
    /// <summary>Events read for nothing: turn and call boundaries, streamed tool arguments, partial output, session housekeeping.</summary>
    static readonly HashSet<string> Passed =
    [
        "assistant.turn_start", "assistant.turn_end", "assistant.message_start", "assistant.tool_call_delta", "assistant.idle",
        "tool.execution_partial_result", "model.call_finished", "model.call_final_result", "instruction.update", "prompt_cache_break",
        "session.info", "session.tools_updated", "session.background_tasks_changed", "session.mcp_server_status_changed",
        "session.mcp_server_tools_loaded", "session.mcp_servers_loaded",
    ];

    readonly Dictionary<string, string> summaries = [];

    public override TranscriptFormat Format => TranscriptFormat.CopilotCli;

    protected override void Read(JsonObject record)
    {
        var at = At(record);
        var type = TypeOf(record);
        var data = record["data"] as JsonObject;
        Model ??= Text(data?["model"]);
        switch (type)
        {
            case "user.message":
                Add(AgentTurnKind.Prompt, at, Text(data?["content"]));
                break;
            case "assistant.message_delta":
                Append(AgentTurnKind.Message, Text(data?["messageId"]), at, Text(data?["deltaContent"]));
                break;
            case "assistant.message":
                Complete(AgentTurnKind.Message, Text(data?["messageId"]), at, Text(data?["content"]));
                foreach (var request in (data?["toolRequests"] as JsonArray ?? []).OfType<JsonObject>())
                    if (Text(request["toolCallId"]) is { } id && Text(request["intentionSummary"]) is { } summary)
                        summaries[id] = summary;
                break;
            case "assistant.reasoning_delta":
                Append(AgentTurnKind.Reasoning, Text(data?["reasoningId"]), at, Text(data?["deltaContent"]));
                break;
            case "assistant.reasoning":
                Complete(AgentTurnKind.Reasoning, Text(data?["reasoningId"]), at, Text(data?["content"]));
                break;
            case "tool.execution_start" when Text(data?["toolCallId"]) is { } id:
                ToolStarted(new Turn(AgentTurnKind.Tool, at, null)
                {
                    Name = Text(data?["toolName"]),
                    Summary = (summaries.Remove(id, out var intention) ? intention : null) ?? Text(data?["arguments"]?["description"]),
                    Arguments = Pretty(data?["arguments"]),
                }, id);
                break;
            case "tool.shell_output":
                ShellOutput(Find(Text(data?["toolCallId"])), Text(data?["text"]));
                break;
            case "tool.execution_complete":
                var callId = Text(data?["toolCallId"]);
                var tool = Find(callId) ?? Add(AgentTurnKind.Tool, at, null);
                tool.Output = Text(data?["result"]?["content"]) ?? Text(data?["error"]?["message"]) ?? tool.Output;
                tool.Failed = Flag(data?["success"]) == false || Number(data?["shellExecution"]?["exitCode"]) is not (null or 0);
                ToolFinished(tool, callId, at);
                break;
            case "model.call_start":
                ModelCalls++;
                break;
            case "session.usage_checkpoint":
                // Totals so far: the last checkpoint holds the session's.
                var models = (data?["accountingSnapshot"]?["modelMetrics"] as JsonObject)?.Select(m => m.Value?["usage"]).ToList() ?? [];
                InputTokens = models.Sum(u => Number(u?["inputTokens"]) ?? 0);
                OutputTokens = models.Sum(u => Number(u?["outputTokens"]) ?? 0);
                break;
            case "session.warning":
                Add(AgentTurnKind.Notice, at, Text(data?["message"]));
                Said(AgentTurnKind.Notice, at, Text(data?["message"]));
                break;
            case "session.error":
                var error = Text(data?["message"]) ?? "session error";
                Add(AgentTurnKind.Error, at, error).Failed = true;
                Said(AgentTurnKind.Error, at, error);
                break;
            case "result":
                DurationMs = Number(record["usage"]?["sessionDurationMs"]);
                Add(AgentTurnKind.Result, at, null).Failed = Number(record["exitCode"]) is not (null or 0);
                break;
            case { } known when Passed.Contains(known):
                break;
            default:
                Unread(type ?? "(no type)");
                break;
        }
    }
}

/// <summary>Claude Code's stream-json (<c>claude -p --output-format stream-json --verbose</c>).</summary>
sealed class ClaudeReader : TranscriptReader
{
    readonly HashSet<string> messages = [];

    public override TranscriptFormat Format => TranscriptFormat.ClaudeCode;

    protected override void Read(JsonObject record)
    {
        var at = At(record);
        var type = TypeOf(record);
        switch (type)
        {
            case "system":
                Model ??= Text(record["model"]);
                break;
            case "assistant":
                var message = record["message"];
                Model ??= Text(message?["model"]);
                if (Text(message?["id"]) is { } id && messages.Add(id))
                    ModelCalls++;
                foreach (var block in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
                    switch (Text(block["type"]))
                    {
                        case "text":
                            Add(AgentTurnKind.Message, at, Text(block["text"]));
                            Said(AgentTurnKind.Message, at, Text(block["text"]));
                            break;
                        case "thinking":
                            Add(AgentTurnKind.Reasoning, at, Text(block["thinking"]));
                            Said(AgentTurnKind.Reasoning, at, Text(block["thinking"]));
                            break;
                        case "tool_use" when Text(block["id"]) is { } toolId:
                            ToolStarted(new Turn(AgentTurnKind.Tool, at, null)
                            {
                                Name = Text(block["name"]),
                                Summary = Text(block["input"]?["description"]),
                                Arguments = Pretty(block["input"]),
                            }, toolId);
                            break;
                        case var other:
                            Unread($"assistant.{other}");
                            break;
                    }
                break;
            case "user":
                var content = record["message"]?["content"];
                if (Text(content) is { } prompt)
                    Add(AgentTurnKind.Prompt, at, prompt);
                foreach (var block in (content as JsonArray ?? []).OfType<JsonObject>())
                    switch (Text(block["type"]))
                    {
                        case "tool_result":
                            var callId = Text(block["tool_use_id"]);
                            var tool = Find(callId) ?? Add(AgentTurnKind.Tool, at, null);
                            tool.Output = ResultText(block["content"]);
                            tool.Failed = Flag(block["is_error"]) == true;
                            ToolFinished(tool, callId, at);
                            break;
                        case "text":
                            Add(AgentTurnKind.Prompt, at, Text(block["text"]));
                            break;
                        case var other:
                            Unread($"user.{other}");
                            break;
                    }
                break;
            case "result":
                DurationMs = Number(record["duration_ms"]);
                var usage = record["usage"];
                InputTokens = (Number(usage?["input_tokens"]) ?? 0) + (Number(usage?["cache_creation_input_tokens"]) ?? 0) + (Number(usage?["cache_read_input_tokens"]) ?? 0);
                OutputTokens = Number(usage?["output_tokens"]);
                Add(AgentTurnKind.Result, at, Text(record["result"]) ?? Text(record["api_error"])).Failed = Flag(record["is_error"]) == true;
                break;
            case "stream_event":
                // Partial messages: the assistant record that follows carries the whole.
                break;
            default:
                Unread(type ?? "(no type)");
                break;
        }
    }

    /// <summary>A tool result's content: text, or a list of blocks whose text parts are joined.</summary>
    static string? ResultText(JsonNode? content) => Text(content)
        ?? (content is JsonArray parts ? string.Join("\n", parts.OfType<JsonObject>().Select(p => Text(p["text"])).OfType<string>()) : null);
}
