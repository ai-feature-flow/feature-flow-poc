using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Api.Contracts;

namespace FeatureFlow.Api;

/// <summary>
/// An agent session's transcript, the runtime's own output, read as turns (console A4): Copilot CLI's JSON-lines session
/// events or Claude Code's stream-json. Streaming deltas are merged into the message they build, or dropped where a final
/// event carries the whole; event types read for nothing are passed over; any other, and any line that is not a JSON
/// object, is counted as unread, never fatal.
/// </summary>
public static class Transcripts
{
    /// <summary>The transcript's turns, or null when no line is an event of a runtime this reads.</summary>
    public static TranscriptTurns? Read(string text)
    {
        var records = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(ObjectOf).ToList();
        // Copilot's event types are dotted (user.message); Claude Code's are single words.
        Reader? reader = records.Any(r => TypeOf(r)?.Contains('.', StringComparison.Ordinal) == true) ? new CopilotReader()
            : records.Any(r => TypeOf(r) is "system" or "assistant" or "user") ? new ClaudeReader()
            : null;
        if (reader is null)
            return null;
        foreach (var record in records)
            if (record is null)
                reader.Unread("(unreadable line)");
            else
                reader.Read(record);
        return reader.Turns();
    }

    static JsonObject? ObjectOf(string line)
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

    static string? TypeOf(JsonObject? record) => Text(record?["type"]);

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A tool's arguments as indented JSON text; shown as text, so nothing in them needs escaping beyond JSON's own.</summary>
    static string? Pretty(JsonNode? node) => node?.ToJsonString(Indented);

    sealed class Turn(TranscriptTurnKind kind, DateTimeOffset? at, string? text)
    {
        public TranscriptTurnKind Kind { get; } = kind;
        public DateTimeOffset? At { get; } = at;
        public StringBuilder Text { get; } = new(text);
        public string? Name { get; init; }
        public string? Summary { get; set; }
        public string? Arguments { get; init; }
        public string? Output { get; set; }
        public StringBuilder Shell { get; } = new();
        public bool Failed { get; set; }

        public TranscriptTurn ToTurn() => new(Kind, At, Kind == TranscriptTurnKind.Tool || Text.Length == 0 ? null : Text.ToString(),
            Kind == TranscriptTurnKind.Tool ? new TranscriptTool(Name ?? "tool", Summary, Arguments, Output ?? (Shell.Length > 0 ? Shell.ToString() : null)) : null,
            Failed);
    }

    abstract class Reader
    {
        readonly List<Turn> turns = [];
        readonly Dictionary<string, Turn> byId = [];
        readonly Dictionary<string, int> unread = [];
        protected string? Model;
        protected DateTimeOffset? Started, Last;
        protected long? DurationMs, InputTokens, OutputTokens;
        protected int ToolCalls, ModelCalls;

        public abstract void Read(JsonObject record);

        public void Unread(string type) => unread[type] = unread.GetValueOrDefault(type) + 1;

        protected Turn Add(TranscriptTurnKind kind, DateTimeOffset? at, string? text, string? id = null)
        {
            var turn = new Turn(kind, at, text);
            turns.Add(turn);
            if (id is not null)
                byId[id] = turn;
            return turn;
        }

        protected Turn Add(Turn turn, string id)
        {
            turns.Add(turn);
            byId[id] = turn;
            return turn;
        }

        protected Turn? Find(string? id) => id is not null && byId.TryGetValue(id, out var turn) ? turn : null;

        /// <summary>A streamed piece of the turn <paramref name="id"/> names: the turn starts where its first piece arrives.</summary>
        protected void Append(TranscriptTurnKind kind, string? id, DateTimeOffset? at, string? piece)
        {
            if (id is null)
                return;
            (Find(kind + id) ?? Add(kind, at, null, kind + id)).Text.Append(piece);
        }

        /// <summary>The whole of the turn <paramref name="id"/> names: it replaces the pieces streamed, keeping their place.</summary>
        protected void Complete(TranscriptTurnKind kind, string? id, DateTimeOffset? at, string? whole)
        {
            if (string.IsNullOrEmpty(whole))
                return;
            if (id is not null && Find(kind + id) is { } streamed)
                streamed.Text.Clear().Append(whole);
            else
                Add(kind, at, whole, id is null ? null : kind + id);
        }

        protected DateTimeOffset? At(JsonObject record)
        {
            var at = DateTimeOffset.TryParse(Text(record["timestamp"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                ? t : (DateTimeOffset?)null;
            Started ??= at;
            Last = at ?? Last;
            return at;
        }

        public TranscriptTurns Turns() => new(
            this is CopilotReader ? TranscriptRuntime.CopilotCli : TranscriptRuntime.ClaudeCode,
            Model, Started, DurationMs ?? (long?)(Last - Started)?.TotalMilliseconds, ToolCalls, ModelCalls, InputTokens, OutputTokens,
            turns.Select(t => t.ToTurn()).ToList(),
            unread.Select(u => new UnreadEvents(u.Key, u.Value)).ToList());
    }

    /// <summary>Copilot CLI's JSON-lines session events (<c>copilot --output-format json</c>).</summary>
    sealed class CopilotReader : Reader
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

        public override void Read(JsonObject record)
        {
            var at = At(record);
            var type = TypeOf(record);
            var data = record["data"] as JsonObject;
            Model ??= Text(data?["model"]);
            switch (type)
            {
                case "user.message":
                    Add(TranscriptTurnKind.Prompt, at, Text(data?["content"]));
                    break;
                case "assistant.message_delta":
                    Append(TranscriptTurnKind.Message, Text(data?["messageId"]), at, Text(data?["deltaContent"]));
                    break;
                case "assistant.message":
                    Complete(TranscriptTurnKind.Message, Text(data?["messageId"]), at, Text(data?["content"]));
                    foreach (var request in (data?["toolRequests"] as JsonArray ?? []).OfType<JsonObject>())
                        if (Text(request["toolCallId"]) is { } id && Text(request["intentionSummary"]) is { } summary)
                            summaries[id] = summary;
                    break;
                case "assistant.reasoning_delta":
                    Append(TranscriptTurnKind.Reasoning, Text(data?["reasoningId"]), at, Text(data?["deltaContent"]));
                    break;
                case "assistant.reasoning":
                    Complete(TranscriptTurnKind.Reasoning, Text(data?["reasoningId"]), at, Text(data?["content"]));
                    break;
                case "tool.execution_start" when Text(data?["toolCallId"]) is { } id:
                    ToolCalls++;
                    Add(new Turn(TranscriptTurnKind.Tool, at, null)
                    {
                        Name = Text(data?["toolName"]),
                        Summary = summaries.GetValueOrDefault(id) ?? Text(data?["arguments"]?["description"]),
                        Arguments = Pretty(data?["arguments"]),
                    }, id);
                    break;
                case "tool.shell_output":
                    Find(Text(data?["toolCallId"]))?.Shell.Append(Text(data?["text"]));
                    break;
                case "tool.execution_complete":
                    var tool = Find(Text(data?["toolCallId"])) ?? Add(TranscriptTurnKind.Tool, at, null);
                    tool.Output = Text(data?["result"]?["content"]) ?? Text(data?["error"]?["message"]) ?? tool.Output;
                    tool.Failed = Flag(data?["success"]) == false || Number(data?["shellExecution"]?["exitCode"]) is not (null or 0);
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
                    Add(TranscriptTurnKind.Notice, at, Text(data?["message"]));
                    break;
                case "session.error":
                    Add(TranscriptTurnKind.Error, at, Text(data?["message"]) ?? "session error").Failed = true;
                    break;
                case "result":
                    DurationMs = Number(record["usage"]?["sessionDurationMs"]);
                    Add(TranscriptTurnKind.Result, at, null).Failed = Number(record["exitCode"]) is not (null or 0);
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
    sealed class ClaudeReader : Reader
    {
        readonly HashSet<string> messages = [];

        public override void Read(JsonObject record)
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
                                Add(TranscriptTurnKind.Message, at, Text(block["text"]));
                                break;
                            case "thinking":
                                Add(TranscriptTurnKind.Reasoning, at, Text(block["thinking"]));
                                break;
                            case "tool_use" when Text(block["id"]) is { } toolId:
                                ToolCalls++;
                                Add(new Turn(TranscriptTurnKind.Tool, at, null)
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
                        Add(TranscriptTurnKind.Prompt, at, prompt);
                    foreach (var block in (content as JsonArray ?? []).OfType<JsonObject>())
                        switch (Text(block["type"]))
                        {
                            case "tool_result":
                                var tool = Find(Text(block["tool_use_id"])) ?? Add(TranscriptTurnKind.Tool, at, null);
                                tool.Output = ResultText(block["content"]);
                                tool.Failed = Flag(block["is_error"]) == true;
                                break;
                            case "text":
                                Add(TranscriptTurnKind.Prompt, at, Text(block["text"]));
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
                    Add(TranscriptTurnKind.Result, at, Text(record["result"]) ?? Text(record["api_error"])).Failed = Flag(record["is_error"]) == true;
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
}
