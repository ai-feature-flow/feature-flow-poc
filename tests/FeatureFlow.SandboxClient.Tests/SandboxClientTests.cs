using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient.Tests;

/// <summary>
/// The client in the sandbox (docs/02 section 4.2): it moves to another replica on a connection error, keeps the
/// question's id across retries (LOG 59), never gives the agent an infrastructure error (LOG 60, SP2 N1), and its
/// hook command holds rather than denies when no replica answers (R19, SP2 N2).
/// </summary>
[Collection("environment")]
public sealed class SandboxClientTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), $"ff-client-{Guid.NewGuid():N}");
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SandboxClientTests()
    {
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("FF_STATE", folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FF_STATE", null);
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Replicas the test controls: a URL is down while its predicate says so; every request is kept.</summary>
    sealed class Replicas(Func<HttpRequestMessage, string, JsonNode?> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.RequestUri!.ToString(), body));
            var reply = answer(request, body) ?? throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    EngineClient Client(Replicas replicas, double budgetSeconds = 30) =>
        new(["http://a", "http://b"], "token", TimeSpan.FromSeconds(budgetSeconds), new ClientLog(Path.Combine(folder, "client.jsonl")), replicas)
        {
            Pause = TimeSpan.FromMilliseconds(10),
        };

    static async Task<List<JsonNode>> CallAsync(McpServer server, string tool, JsonObject arguments)
    {
        var input = new StringReader(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() }.ToJsonString() + "\n" +
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } }.ToJsonString() + "\n");
        var output = new StringWriter();
        await server.RunAsync(input, output, CancellationToken.None);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
    }

    [Fact]
    public async Task A_question_keeps_its_id_across_replicas_and_returns_the_answer()
    {
        var failedOnce = false;
        var replicas = new Replicas((request, body) =>
        {
            if (request.RequestUri!.Host == "a" && !failedOnce)
            {
                failedOnce = true;
                return null;
            }

            return request.Method == HttpMethod.Post
                ? new JsonObject { ["status"] = "open" }
                : new JsonObject { ["status"] = "answered", ["answer"] = "teal-1" };
        });
        var server = new McpServer(Client(replicas), new McpSettings(folder, null, Questions: true, Checkpoint: false), new ClientLog(null)) { PollSeconds = 1 };
        var replies = await CallAsync(server, "ask_human", new JsonObject { ["question"] = "Which?" });

        var result = replies.Single(r => r["id"]?.GetValue<int>() == 2)["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("The person answered: teal-1", result["content"]![0]!["text"]!.ToString());
        var opens = replicas.Requests.Where(r => r.Url.EndsWith("/agent/v1/questions", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, opens.Count);
        Assert.Single(opens.Select(o => JsonNode.Parse(o.Body)!["questionId"]!.ToString()).Distinct());
        Assert.Contains("\"connection_failed\"", await File.ReadAllTextAsync(Path.Combine(folder, "client.jsonl"), Ct));
    }

    /// <summary>SP2 N1 inverted: an engine that stays unreachable ends the session; the agent never sees an error result.</summary>
    [Fact]
    public async Task An_engine_unreachable_past_the_budget_ends_the_session_and_gives_the_agent_no_error()
    {
        var ended = false;
        var server = new McpServer(Client(new Replicas((_, _) => null), budgetSeconds: 0.3), new McpSettings(folder, null, false, false), new ClientLog(null),
            () => ended = true);
        var replies = await CallAsync(server, "report", new JsonObject { ["outcome"] = "implemented", ["summary"] = "x" });

        Assert.True(ended);
        Assert.DoesNotContain(replies, r => r["id"]?.GetValue<int>() == 2);
        Assert.True(File.Exists(Path.Combine(folder, "engine_unreachable")));
    }

    [Fact]
    public async Task A_report_names_the_artifacts_missing_from_the_output_folder()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "present.md"), "x", Ct);
        var replicas = new Replicas((_, _) => new JsonObject { ["accepted"] = false, ["message"] = "Report rejected" });
        var server = new McpServer(Client(replicas), new McpSettings(folder, null, false, false), new ClientLog(null));
        await CallAsync(server, "report", new JsonObject
        {
            ["outcome"] = "implemented", ["summary"] = "x", ["artifacts"] = new JsonObject { ["a"] = "present.md", ["b"] = "absent.md" },
        });

        var sent = JsonNode.Parse(Assert.Single(replicas.Requests).Body)!;
        Assert.Equal(["absent.md"], sent["missing"]!.AsArray().Select(m => m!.ToString()));
    }

    [Fact]
    public async Task The_hook_command_applies_a_denial_with_its_reason()
    {
        var replicas = new Replicas((_, _) => new JsonObject { ["decision"] = "deny", ["reason"] = "never push" });
        var error = new StringWriter();
        var exit = await HookCommand.RunAsync("pre_tool_use", Client(replicas), new StringReader("""{"tool_name":"Bash"}"""), new StringWriter(), error,
            new ClientLog(null), Ct);
        Assert.Equal(2, exit);
        Assert.Contains("never push", error.ToString());
    }

    /// <summary>Review finding 6: an answer with no decision (an expired or revoked token's 401) blocks the call; it never allows it.</summary>
    [Fact]
    public async Task The_hook_command_fails_closed_on_an_answer_with_no_decision()
    {
        var replicas = new Replicas((_, _) => new JsonObject { ["error"] = "unknown or revoked step token" });
        var error = new StringWriter();
        var exit = await HookCommand.RunAsync("pre_tool_use", Client(replicas), new StringReader("""{"tool_name":"Bash"}"""), new StringWriter(), error,
            new ClientLog(null), Ct);
        Assert.Equal(2, exit);
        Assert.Contains("revoked", error.ToString());
    }

    /// <summary>Re-check N3: a hook that gates nothing (Stop) does not block when it gets no decision, or the runtime could not stop.</summary>
    [Theory]
    [InlineData("stop")]
    [InlineData("post_tool_use")]
    [InlineData("session_start")]
    public async Task A_hook_that_gates_nothing_does_not_block_on_an_answer_with_no_decision(string hook)
    {
        var replicas = new Replicas((_, _) => new JsonObject { ["error"] = "unknown or revoked step token" });
        var exit = await HookCommand.RunAsync(hook, Client(replicas), new StringReader("{}"), new StringWriter(), new StringWriter(), new ClientLog(null), Ct);
        Assert.Equal(0, exit);
    }

    /// <summary>R19 (SP2 S4 and N2): a hook that cannot reach the engine holds, then stops the session; it never denies.</summary>
    [Fact]
    public async Task The_hook_command_holds_while_no_replica_answers_and_then_stops_the_session_rather_than_deny()
    {
        var output = new StringWriter();
        var started = DateTime.UtcNow;
        var exit = await HookCommand.RunAsync("pre_tool_use", Client(new Replicas((_, _) => null), budgetSeconds: 0.5), new StringReader("{}"), output,
            new StringWriter(), new ClientLog(null), Ct);
        Assert.Equal(0, exit);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(0.5));
        Assert.False(JsonNode.Parse(output.ToString())!["continue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_hook_command_reaches_a_replica_that_comes_back_within_the_hold()
    {
        var calls = 0;
        var replicas = new Replicas((_, _) => ++calls < 5 ? null : new JsonObject { ["decision"] = "allow" });
        var output = new StringWriter();
        var exit = await HookCommand.RunAsync("pre_tool_use", Client(replicas), new StringReader("{}"), output, new StringWriter(), new ClientLog(null), Ct);
        Assert.Equal(0, exit);
        Assert.Equal("", output.ToString());
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_a_failure_to_retry_not_an_answer()
    {
        var calls = 0;
        var handler = new Raw(() => ++calls == 1 ? "" : """{"decision":"allow"}""");
        var client = new EngineClient(["http://a"], "t", TimeSpan.FromSeconds(10), new ClientLog(null), handler) { Pause = TimeSpan.FromMilliseconds(10) };
        var reply = await client.SendAsync(HttpMethod.Post, "/agent/v1/hooks/pre_tool_use", new JsonObject(), TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("allow", reply["decision"]!.ToString());
        Assert.Equal(2, calls);
    }

    sealed class Raw(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) });
    }
}
