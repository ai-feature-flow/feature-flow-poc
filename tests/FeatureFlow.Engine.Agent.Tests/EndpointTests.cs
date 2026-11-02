using System.Net;
using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// The engine tools on the agent worker (docs/04 section 3.3, 3.4): correction turns for the report, and questions
/// opened idempotently by the id the sandbox client gives (LOG 59). State is only in the record store, so two
/// replicas are two calls with the same store.
/// </summary>
public sealed class EndpointTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), $"ff-endpoint-{Guid.NewGuid():N}");
    readonly SqliteRecordStore store;
    readonly Signals signals = new();
    readonly AgentEndpointServices services;
    static readonly StepClaims Claims = new("run-1/implement/1", "run-1", "run-1", "implement", "branch", ["read", "shell"], 0);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Signals : IRunSignals
    {
        public List<QuestionSignal> Sent { get; } = [];

        public Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken)
        {
            Sent.Add(question);
            return Task.CompletedTask;
        }
    }

    public EndpointTests()
    {
        Directory.CreateDirectory(folder);
        store = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        services = new AgentEndpointServices(StepTokens.FromSecret("k"), store, signals, null, TimeSpan.FromMilliseconds(20));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    async Task StepAsync(int turns = 2, int questions = 2) =>
        (await store.AppendAsync(new RecordBatch(AgentSteps:
        [
            new AgentStepRecord(Claims.StepKey, "run-1", "run-1", "implement", "ticket", ReportAndHookTests.Contract, turns, questions, 3600, DateTimeOffset.UtcNow),
        ]), Ct)).Unwrap();

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    const string Invalid = """{"report":{"outcome":"done","summary":"x"}}""";
    const string Valid = """{"report":{"outcome":"implemented","summary":"done","outputs":{"answer":"x","count":1},"artifacts":{"notes":"notes.md"}}}""";

    [Fact]
    public async Task Two_rejected_reports_get_correction_turns_and_a_third_fails_the_attempt()
    {
        await StepAsync(turns: 2);
        var first = await AgentEndpoint.ReportAsync(services, Claims, Json(Invalid), Ct);
        var second = await AgentEndpoint.ReportAsync(services, Claims, Json(Invalid), Ct);
        var third = await AgentEndpoint.ReportAsync(services, Claims, Json(Invalid), Ct);
        var fourth = await AgentEndpoint.ReportAsync(services, Claims, Json(Valid), Ct);

        Assert.Contains("correction turn 1 of 2", first.Message);
        Assert.Contains("correction turn 2 of 2", second.Message);
        Assert.False(second.Stop);
        Assert.True(third is { Accepted: false, Stop: true });
        Assert.Contains("attempt has failed", third.Message);
        // After the attempt has failed, even a valid report is not taken.
        Assert.True(fourth is { Accepted: false, Stop: true });
        var records = (await store.QueryAsync<ReportRecord>(Queries.Reports, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public async Task A_corrected_report_is_accepted()
    {
        await StepAsync();
        Assert.False((await AgentEndpoint.ReportAsync(services, Claims, Json(Invalid), Ct)).Accepted);
        var accepted = await AgentEndpoint.ReportAsync(services, Claims, Json(Valid), Ct);
        Assert.True(accepted is { Accepted: true, Stop: true });
    }

    [Fact]
    public async Task A_question_opened_twice_with_one_id_is_one_question_signalled_once()
    {
        await StepAsync();
        var body = Json("""{"questionId":"q-1","question":"Which?","options":["a","b"]}""");
        Assert.Equal(QuestionRecord.Open, (await AgentEndpoint.OpenQuestionAsync(services, Claims, body, Ct)).Status);
        // A retry against another replica: same store, same id.
        Assert.Equal(QuestionRecord.Open, (await AgentEndpoint.OpenQuestionAsync(services, Claims, body, Ct)).Status);
        Assert.Single(signals.Sent);
        Assert.Single((await store.QueryAsync<QuestionRecord>(Queries.Questions, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap());
    }

    [Fact]
    public async Task A_long_poll_returns_the_answer_the_run_recorded()
    {
        await StepAsync();
        await AgentEndpoint.OpenQuestionAsync(services, Claims, Json("""{"questionId":"q-2","question":"Which?"}"""), Ct);
        var poll = AgentEndpoint.PollQuestionAsync(services, Claims, "q-2", TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(100, Ct);
        (await store.AppendAsync(new RecordBatch(Questions: [new QuestionRecord(Claims.StepKey, "run-1", "q-2", "Which?", [], QuestionRecord.Answered, "teal-1", DateTimeOffset.UtcNow)]), Ct)).Unwrap();
        var reply = await poll;
        Assert.Equal(("answered", "teal-1"), (reply.Status, reply.Answer));
        // A late "open" from a retried call does not reopen an answered question.
        await AgentEndpoint.OpenQuestionAsync(services, Claims, Json("""{"questionId":"q-2","question":"Which?"}"""), Ct);
        Assert.Equal("answered", (await AgentEndpoint.PollQuestionAsync(services, Claims, "q-2", TimeSpan.Zero, Ct)).Status);
    }

    [Fact]
    public async Task Past_the_question_limit_the_agent_is_told_to_use_its_judgement()
    {
        await StepAsync(questions: 1);
        await AgentEndpoint.OpenQuestionAsync(services, Claims, Json("""{"questionId":"q-3","question":"One?"}"""), Ct);
        var second = await AgentEndpoint.OpenQuestionAsync(services, Claims, Json("""{"questionId":"q-4","question":"Two?"}"""), Ct);
        Assert.Equal("limit", second.Status);
        Assert.Contains("use your judgement", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(signals.Sent);
    }

    [Fact]
    public async Task A_hook_call_is_decided_from_the_token_and_recorded()
    {
        await StepAsync();
        var decision = await AgentEndpoint.HookAsync(services, Claims, "pre_tool_use", Json("""{"tool_name":"Bash","tool_input":{"command":"git push"}}"""), Ct);
        Assert.Equal("deny", decision.Decision);
        var events = (await store.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash deny", StringComparison.Ordinal));
    }

    sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static DefaultHttpContext Request(string authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = authorization;
        context.Request.Headers["anthropic-beta"] = "tools-2024";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"haiku","stream":true}"""));
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>F6 in proxy mode (SP5): the step's token is replaced by the real credential, which goes only upstream.</summary>
    [Theory]
    [InlineData("api_key")]
    [InlineData("oauth")]
    public async Task The_model_proxy_replaces_the_step_token_with_the_credential(string auth)
    {
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: message_delta\ndata: {\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}\n\n"),
        });
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), auth, _ => Task.FromResult<string?>("real-credential")), upstream);
        var context = Request("Bearer step-token");
        var (status, input, output) = await proxy.ForwardAsync(context, "v1/messages", Ct);

        var sent = Assert.Single(upstream.Seen);
        Assert.Equal("https://model.example/v1/messages", sent.RequestUri!.ToString());
        var headers = sent.Headers.ToString();
        Assert.DoesNotContain("step-token", headers);
        if (auth == "oauth")
        {
            Assert.Equal("Bearer real-credential", sent.Headers.Authorization!.ToString());
            Assert.Contains(ModelProxy.OAuthBeta, sent.Headers.GetValues("anthropic-beta").Single());
        }
        else
        {
            Assert.Equal("real-credential", sent.Headers.GetValues("x-api-key").Single());
            Assert.Null(sent.Headers.Authorization);
        }

        Assert.Equal((200, 12L, 34L), (status, input, output));
        context.Response.Body.Position = 0;
        Assert.Contains("message_delta", await new StreamReader(context.Response.Body).ReadToEndAsync(Ct));
    }

    /// <summary>
    /// Review finding 1: the agent controls the path after <c>/model/</c>. Only the Messages API's relative paths, by
    /// POST, reach the upstream; anything else is refused before the credential is even read, so it is never sent.
    /// </summary>
    [Theory]
    [InlineData("POST", "https://evil.example/steal")]
    [InlineData("POST", "//evil.example/steal")]
    [InlineData("POST", "http://evil.example/v1/messages")]
    [InlineData("POST", "v1/models")]
    [InlineData("POST", "v1/messages/../../steal")]
    [InlineData("GET", "v1/messages")]
    public async Task The_model_proxy_refuses_any_request_but_the_messages_api_and_never_sends_the_credential(string method, string path)
    {
        var read = false;
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), "api_key", _ =>
        {
            read = true;
            return Task.FromResult<string?>("real-credential");
        }), upstream);
        var context = Request("Bearer step-token");
        context.Request.Method = method;
        var (status, _, _) = await proxy.ForwardAsync(context, path, Ct);

        Assert.Equal(404, status);
        Assert.Empty(upstream.Seen);
        Assert.False(read);
    }

    [Fact]
    public void The_proxy_s_target_keeps_the_upstream_s_scheme_host_and_port()
    {
        var upstream = new Uri("https://model.example:8443/");
        Assert.Equal("https://model.example:8443/v1/messages?beta=true", ModelProxy.Target(upstream, "POST", "v1/messages", "?beta=true")!.ToString());
        Assert.Null(ModelProxy.Target(upstream, "POST", "https://evil.example/v1/messages", ""));
    }

    /// <summary>Review finding 11: when the step's attempt ends, its token stops working.</summary>
    [Fact]
    public async Task A_revoked_step_s_token_is_refused()
    {
        await StepAsync();
        Assert.True(await AgentEndpoint.ActiveAsync(services, Claims));
        (await store.AppendAsync(new RecordBatch(AgentSteps:
        [
            new AgentStepRecord(Claims.StepKey, "run-1", "run-1", "implement", "ticket", ReportAndHookTests.Contract, 2, 2, 3600, DateTimeOffset.UtcNow, Revoked: true),
        ]), Ct)).Unwrap();
        Assert.False(await AgentEndpoint.ActiveAsync(services, Claims));
        Assert.False(await AgentEndpoint.ActiveAsync(services, Claims with { StepKey = "run-1/other/1" }));
    }

    /// <summary>Review finding 12: a step cannot read another step's question by opening it again.</summary>
    [Fact]
    public async Task A_question_of_another_step_is_not_shown()
    {
        await StepAsync();
        (await store.AppendAsync(new RecordBatch(Questions: [new QuestionRecord("run-1/other/1", "run-1", "q-9", "Secret?", [], QuestionRecord.Answered, "hidden", DateTimeOffset.UtcNow)]), Ct)).Unwrap();
        var reply = await AgentEndpoint.OpenQuestionAsync(services, Claims, Json("""{"questionId":"q-9","question":"x"}"""), Ct);
        Assert.Equal(("unknown", null), (reply.Status, reply.Answer));
    }

    [Fact]
    public async Task With_the_model_unreachable_the_proxy_answers_502_and_never_the_credential()
    {
        var upstream = new Upstream(_ => throw new HttpRequestException("refused"));
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), "api_key", _ => Task.FromResult<string?>("real-credential")), upstream);
        var context = Request("Bearer step-token");
        var (status, _, _) = await proxy.ForwardAsync(context, "v1/messages", Ct);
        Assert.Equal(502, status);
        context.Response.Body.Position = 0;
        Assert.DoesNotContain("real-credential", await new StreamReader(context.Response.Body).ReadToEndAsync(Ct));
    }
}
