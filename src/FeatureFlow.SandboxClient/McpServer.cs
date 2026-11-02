using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient;

/// <summary>Which of the engine tools a step is offered (docs/04 section 3.3), and where its output folder and checkout are.</summary>
public sealed record McpSettings(string Output, string? Checkout, bool Questions, bool Checkpoint)
{
    public static McpSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("FF_OUTPUT") ?? "/ff/out",
        Environment.GetEnvironmentVariable("FF_CHECKOUT"),
        Environment.GetEnvironmentVariable("FF_QUESTIONS") == "true",
        Environment.GetEnvironmentVariable("FF_WRITE") == "true");
}

/// <summary>
/// The client in the sandbox (docs/02 section 4.2): a local stdio MCP server that serves the engine tools to the
/// runtime and forwards each call to an agent worker replica. It assigns each question's id once, before the
/// first attempt, so a retry against another replica resumes the same question (LOG 59). It never returns an
/// infrastructure error to the agent: when the engine stays unreachable it ends the session resumably (LOG 60).
/// </summary>
public sealed class McpServer(EngineClient engine, McpSettings settings, ClientLog log, Action? onUnreachable = null)
{
    readonly SemaphoreSlim writeGate = new(1, 1);
    TextWriter output = TextWriter.Null;

    /// <summary>How long one long-poll for an answer waits on the endpoint.</summary>
    public int PollSeconds { get; init; } = 25;

    public async Task RunAsync(TextReader input, TextWriter writer, CancellationToken cancellationToken)
    {
        output = writer;
        log.Write("started", ("replicas", engine.CurrentUrl));
        var calls = new List<Task>();
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var method = message?["method"]?.GetValue<string>();
            var id = message?["id"]?.DeepClone();
            switch (method)
            {
                case "initialize":
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id,
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = message?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "engine", ["version"] = "0.1.0" },
                        },
                    });
                    break;
                case "tools/list":
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["tools"] = Tools() } });
                    break;
                case "tools/call":
                    // Each call runs apart, so pings are answered while a question waits for hours.
                    calls.Add(Task.Run(() => CallAsync(id, message!["params"]!, cancellationToken), cancellationToken));
                    break;
                case "ping":
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() });
                    break;
                default:
                    if (id is not null)
                        await SendAsync(new JsonObject
                        {
                            ["jsonrpc"] = "2.0", ["id"] = id,
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" },
                        });
                    break;
            }
        }

        await Task.WhenAll(calls);
    }

    JsonArray Tools()
    {
        var tools = new JsonArray
        {
            Tool("report", "Report this step's result to the engine: one outcome your task declares, a short summary, the outputs your task asks for, " +
                           "and the artifacts you produced. If the engine rejects the report, fix what it names and call report again.",
                new JsonObject
                {
                    ["outcome"] = new JsonObject { ["type"] = "string" },
                    ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "Plain text, at most 2 KB" },
                    ["outputs"] = new JsonObject { ["type"] = "object", ["description"] = "Output name to value, with the types your task gives" },
                    ["artifacts"] = new JsonObject { ["type"] = "object", ["description"] = "Artifact name to its path, relative to your output folder" },
                }, ["outcome", "summary"]),
        };
        if (settings.Questions)
            tools.Add(Tool("ask_human", "Ask a person one question and wait for the answer. It may take hours. If no answer comes, you are told so: then use your judgement.",
                new JsonObject
                {
                    ["question"] = new JsonObject { ["type"] = "string" },
                    ["options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 4 },
                    ["context"] = new JsonObject { ["type"] = "string" },
                }, ["question"]));
        if (settings.Checkpoint)
            tools.Add(Tool("checkpoint", "Commit everything in the checkout now; the engine pushes it. Optional: the engine also detects your commits.",
                new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } }, []));
        return tools;
    }

    static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        },
    };

    async Task CallAsync(JsonNode? id, JsonNode parameters, CancellationToken cancellationToken)
    {
        var name = parameters["name"]?.GetValue<string>() ?? "";
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        string text;
        try
        {
            text = name switch
            {
                "report" => await ReportAsync(arguments, cancellationToken),
                "ask_human" when settings.Questions => await AskAsync(arguments, cancellationToken),
                "checkpoint" when settings.Checkpoint => Checkpoint(arguments),
                _ => $"There is no engine tool named {name}.",
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // LOG 60: the agent is never given an engine error. The session ends, resumably. An unexpected fault in
            // this client is treated the same way: an agent told of it might improvise a result (SP2 N1).
            log.Write("engine_unreachable", ("tool", name), ("error", e.GetType().Name + ": " + e.Message));
            SessionStop.Mark(e.Message);
            (onUnreachable ?? SessionStop.EndRuntime)();
            return;
        }

        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false,
            },
        });
    }

    async Task<string> ReportAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        // The artifacts are checked here, where they are: the endpoint cannot see the sandbox.
        var report = JsonDocument.Parse(arguments.ToJsonString()).RootElement;
        var missing = new JsonArray();
        foreach (var path in Paths(report))
        {
            var full = Path.GetFullPath(Path.Combine(settings.Output, path));
            if (!File.Exists(full) || new FileInfo(full).Length == 0)
                missing.Add(path);
        }

        var reply = await engine.SendAsync(HttpMethod.Post, "/agent/v1/report", new JsonObject { ["report"] = arguments.DeepClone(), ["missing"] = missing },
            TimeSpan.FromSeconds(30), cancellationToken);
        log.Write("reported", ("outcome", arguments["outcome"]?.ToString()), ("accepted", reply["accepted"]?.ToString()));
        return reply["message"]?.GetValue<string>() ?? "The engine did not answer the report.";
    }

    static IEnumerable<string> Paths(JsonElement report) =>
        report.TryGetProperty("artifacts", out var given)
            ? given.ValueKind switch
            {
                JsonValueKind.Object => given.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!),
                JsonValueKind.Array => given.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!),
                _ => [],
            }
            : [];

    /// <summary>The question's id is fixed here, once, before the first attempt (LOG 59).</summary>
    async Task<string> AskAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var questionId = Guid.NewGuid().ToString();
        log.Write("ask", ("question_id", questionId));
        var body = new JsonObject
        {
            ["questionId"] = questionId,
            ["question"] = arguments["question"]?.DeepClone(),
            ["options"] = arguments["options"]?.DeepClone() ?? new JsonArray(),
            ["context"] = arguments["context"]?.DeepClone(),
        };
        var opened = await engine.SendAsync(HttpMethod.Post, "/agent/v1/questions", body, TimeSpan.FromSeconds(30), cancellationToken);
        log.Write("opened", ("question_id", questionId), ("status", opened["status"]?.ToString()), ("replica", engine.CurrentUrl));
        while (true)
        {
            var state = opened["status"]?.GetValue<string>() switch
            {
                "open" => await engine.SendAsync(HttpMethod.Get, $"/agent/v1/questions/{questionId}?wait={PollSeconds}", null,
                    TimeSpan.FromSeconds(PollSeconds + 10), cancellationToken),
                _ => opened,
            };
            switch (state["status"]?.GetValue<string>())
            {
                case "answered":
                    log.Write("answer_received", ("question_id", questionId), ("replica", engine.CurrentUrl));
                    return $"The person answered: {state["answer"]}";
                case "timed_out":
                    log.Write("timed_out_received", ("question_id", questionId));
                    return "No answer came before the timeout. Use your judgement, carry on, and say in your summary what you assumed.";
                case "open":
                    opened = state;
                    continue;
                default:
                    return state["message"]?.GetValue<string>() ?? "The question could not be asked. Use your judgement.";
            }
        }
    }

    string Checkpoint(JsonObject arguments)
    {
        if (settings.Checkout is null)
            return "There is no checkout to commit.";
        var message = arguments["message"]?.GetValue<string>() is { Length: > 0 } m ? m : "Checkpoint";
        Run("git", "add", "-A");
        var (exit, text) = Run("git", "commit", "-m", message);
        return exit == 0 ? "Committed. The engine pushes your commits." : "Nothing was committed: " + text.Trim();
    }

    (int Exit, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(arguments[0]) { WorkingDirectory = settings.Checkout!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments.Skip(1))
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text);
    }

    async Task SendAsync(JsonObject message)
    {
        await writeGate.WaitAsync();
        try
        {
            await output.WriteLineAsync(message.ToJsonString());
            await output.FlushAsync();
        }
        finally
        {
            writeGate.Release();
        }
    }
}
