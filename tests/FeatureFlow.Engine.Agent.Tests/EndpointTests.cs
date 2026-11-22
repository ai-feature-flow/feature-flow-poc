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
    static readonly StepClaims Claims = new("run-1/implement/1", "run-1", "run-1", "implement", "branch", ["read", "shell"], 0, "claude");
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A runtime's reading of its hooks: <c>Bash</c> is a shell call; its denial exits 7 with the reason.</summary>
    sealed class Hooks : IAgentHooks
    {
        public ToolCall Classify(JsonElement hookInput)
        {
            var tool = hookInput.GetProperty("tool_name").GetString()!;
            return new ToolCall(tool, tool == "Bash" ? ToolClass.Shell : null,
                Command: hookInput.TryGetProperty("tool_input", out var i) && i.TryGetProperty("command", out var c) ? c.GetString() : null);
        }

        public HookReply Deny(string reason) => new(7, Error: "refused: " + reason);
    }

    sealed class Signals : IRunSignals
    {
        public List<QuestionSignal> Sent { get; } = [];

        public List<(string WorkflowId, ModelUnreachableSignal Signal)> Unreachable { get; } = [];

        public Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken)
        {
            Sent.Add(question);
            return Task.CompletedTask;
        }

        public Task ModelUnreachableAsync(string workflowId, ModelUnreachableSignal signal, CancellationToken cancellationToken)
        {
            Unreachable.Add((workflowId, signal));
            return Task.CompletedTask;
        }

        public Task<AgentPlanView> PlanAsync(string workflowId, AgentPlanQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PatchPreview> PreviewAsync(string workflowId, PatchPreviewQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PatchPreview> ChildPreviewAsync(string workflowId, ChildPatchPreviewQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    public EndpointTests()
    {
        Directory.CreateDirectory(folder);
        store = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        services = new AgentEndpointServices(StepTokens.FromSecret("k"), store, signals, new Dictionary<string, ModelProxy>(),
            name => name == "claude" ? new Hooks() : null, TimeSpan.FromMilliseconds(20));
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
        // When it opened is kept apart from the question's record, which the run replaces on closing (LOG 106).
        var events = (await store.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();
        Assert.Equal("q-1", Assert.Single(events, e => e.Kind == AgentEndpoint.QuestionOpenedEvent).Detail);
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

    /// <summary>
    /// LOG 444: the step's runtime instance, named in its token, reads the hook input; the policy decides on the class;
    /// a denial carries the runtime's reply. A step whose runtime reads no hooks is denied.
    /// </summary>
    [Fact]
    public async Task A_hook_call_is_read_by_the_step_s_runtime_decided_from_the_token_and_recorded()
    {
        await StepAsync();
        var decision = await AgentEndpoint.HookAsync(services, Claims, "pre_tool_use", Json("""{"tool_name":"Bash","tool_input":{"command":"git push"}}"""), Ct);
        Assert.Equal("deny", decision.Decision);
        Assert.Equal(new HookReply(7, Error: "refused: never push: commit your work and the engine pushes it"), decision.Reply);
        Assert.Equal("allow", (await AgentEndpoint.HookAsync(services, Claims, "pre_tool_use", Json("""{"tool_name":"Bash","tool_input":{"command":"ls"}}"""), Ct)).Decision);
        var events = (await store.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = Claims.StepKey }, Ct)).Unwrap();
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash deny", StringComparison.Ordinal));

        var unread = await AgentEndpoint.HookAsync(services, Claims with { Runtime = "scripted" }, "pre_tool_use", Json("""{"tool_name":"Bash","tool_input":{"command":"ls"}}"""), Ct);
        Assert.Equal(("deny", null), (unread.Decision, unread.Reply));
    }

    /// <summary>LOG 446: a step's model requests go to its runtime instance's proxy; an instance with none has no proxy.</summary>
    [Fact]
    public void A_step_s_model_proxy_is_its_runtime_instance_s()
    {
        ModelProxy Proxy(string upstream) => new(new ModelProxyOptions(new Uri(upstream), "api_key", _ => Task.FromResult<string?>("k")));
        var proxied = services with { Proxies = new Dictionary<string, ModelProxy> { ["claude"] = Proxy("https://a.example/"), ["copilot"] = Proxy("https://b.example/") } };

        Assert.Equal(new Uri("https://b.example/"), proxied.ProxyFor(Claims with { Runtime = "copilot" })!.Upstream);
        Assert.Equal(new Uri("https://a.example/"), proxied.ProxyFor(Claims)!.Upstream);
        Assert.Null(proxied.ProxyFor(Claims with { Runtime = "scripted" }));
        Assert.Null(proxied.ProxyFor(Claims with { Runtime = null }));
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
    [InlineData("github")]
    public async Task The_model_proxy_replaces_the_step_token_with_the_credential(string auth)
    {
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: message_delta\ndata: {\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}\n\n"),
        });
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), auth, _ => Task.FromResult<string?>("real-credential")), upstream);
        var context = Request("Bearer step-token");
        var (status, input, output, model, _) = await proxy.ForwardAsync(context, "v1/messages", Ct);

        var sent = Assert.Single(upstream.Seen);
        Assert.Equal("https://model.example/v1/messages", sent.RequestUri!.ToString());
        var headers = sent.Headers.ToString();
        Assert.DoesNotContain("step-token", headers);
        if (auth == "oauth")
        {
            Assert.Equal("Bearer real-credential", sent.Headers.Authorization!.ToString());
            Assert.Contains(ModelProxy.OAuthBeta, sent.Headers.GetValues("anthropic-beta").Single());
        }
        else if (auth == "github")
        {
            // LOG 496: the GitHub token alone; the client's own beta passes, the proxy adds no Anthropic header.
            Assert.Equal("Bearer real-credential", sent.Headers.Authorization!.ToString());
            Assert.Equal("tools-2024", sent.Headers.GetValues("anthropic-beta").Single());
            Assert.False(sent.Headers.Contains("x-api-key"));
        }
        else
        {
            Assert.Equal("real-credential", sent.Headers.GetValues("x-api-key").Single());
            Assert.Null(sent.Headers.Authorization);
        }

        Assert.Equal((200, 12L, 34L, "haiku"), (status, input, output, model));
        context.Response.Body.Position = 0;
        Assert.Contains("message_delta", await new StreamReader(context.Response.Body).ReadToEndAsync(Ct));
    }

    /// <summary>
    /// Review finding 1: the agent controls the path after <c>/model/</c>. Only the Messages API's relative paths, by
    /// POST, reach the upstream; anything else is refused before the credential is even read, so it is never sent.
    /// </summary>
    [Theory]
    [InlineData("api_key", "POST", "https://evil.example/steal")]
    [InlineData("api_key", "POST", "//evil.example/steal")]
    [InlineData("api_key", "POST", "http://evil.example/v1/messages")]
    [InlineData("api_key", "POST", "v1/models")]
    [InlineData("api_key", "POST", "v1/messages/../../steal")]
    [InlineData("api_key", "GET", "v1/messages")]
    [InlineData("oauth", "GET", "models")]
    [InlineData("api_key", "POST", "chat/completions")]
    [InlineData("github", "POST", "meta")]
    [InlineData("github", "GET", "chat/completions")]
    [InlineData("github", "GET", "copilot_internal/user")]
    [InlineData("github", "POST", "//evil.example/chat/completions")]
    public async Task The_model_proxy_refuses_any_request_but_the_messages_api_and_never_sends_the_credential(string auth, string method, string path)
    {
        var read = false;
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), auth, _ =>
        {
            read = true;
            return Task.FromResult<string?>("real-credential");
        }), upstream);
        var context = Request("Bearer step-token");
        context.Request.Method = method;
        var (status, _, _, _, _) = await proxy.ForwardAsync(context, path, Ct);

        Assert.Equal(404, status);
        Assert.Empty(upstream.Seen);
        Assert.False(read);
    }

    [Fact]
    public void The_proxy_s_target_keeps_the_upstream_s_scheme_host_and_port()
    {
        var upstream = new Uri("https://model.example:8443/");
        Assert.Equal("https://model.example:8443/v1/messages?beta=true", ModelProxy.Target(upstream, "api_key", "POST", "v1/messages", "?beta=true")!.ToString());
        Assert.Null(ModelProxy.Target(upstream, "api_key", "POST", "https://evil.example/v1/messages", ""));
    }

    /// <summary>
    /// Review 4 finding 6: an upstream with a path keeps it, and what would resolve off the upstream's scheme, host or port is
    /// refused. The allow-list's literals never reach that check, so it is tested directly.
    /// </summary>
    [Fact]
    public void An_upstream_s_path_is_kept_and_nothing_resolves_off_its_scheme_host_or_port()
    {
        var upstream = new Uri("https://copilot.example/base/");
        Assert.Equal("https://copilot.example/base/chat/completions?x=1", ModelProxy.Target(upstream, "github", "POST", "chat/completions", "?x=1")!.ToString());
        Assert.Equal("https://copilot.example/base/v1/messages/count_tokens", ModelProxy.Within(upstream, "v1/messages/count_tokens")!.ToString());
        foreach (var away in new[] { "//evil.example/v1/messages", "https://evil.example/base/v1/messages", "http://copilot.example/base/v1/messages", "https://copilot.example:8443/base/" })
            Assert.Null(ModelProxy.Within(upstream, away));
    }

    /// <summary>LOG 104, 496: each auth's allow-list is exactly its documented set, so an added entry fails here.</summary>
    [Theory]
    [InlineData("api_key", new[] { "POST v1/messages", "POST v1/messages/count_tokens" })]
    [InlineData("oauth", new[] { "POST v1/messages", "POST v1/messages/count_tokens" })]
    [InlineData("github", new[] { "GET meta", "GET models", "POST chat/completions", "POST v1/messages", "POST v1/messages/count_tokens" })]
    public void Each_auth_forwards_exactly_its_documented_requests(string auth, string[] requests) =>
        Assert.Equal(requests.Order(StringComparer.Ordinal), ModelProxy.Allowed(auth).Order(StringComparer.Ordinal));

    /// <summary>
    /// LOG 496: with <c>auth: github</c> the proxy also forwards what Copilot CLI asks the Copilot API for: its model
    /// catalogue and <c>chat/completions</c>, whose OpenAI-shaped usage is recorded like the Messages API's, and the
    /// Messages API with its token count.
    /// </summary>
    [Theory]
    [InlineData("GET", "meta")]
    [InlineData("GET", "models")]
    [InlineData("POST", "chat/completions")]
    [InlineData("POST", "v1/messages")]
    [InlineData("POST", "v1/messages/count_tokens")]
    public async Task With_auth_github_the_proxy_forwards_copilot_s_requests_and_reads_their_usage(string method, string path)
    {
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[],"usage":{"prompt_tokens":21,"completion_tokens":8,"total_tokens":29}}"""),
        });
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://api.business.githubcopilot.example/"), "github", _ => Task.FromResult<string?>("github-token")), upstream);
        var context = Request("Bearer step-token");
        context.Request.Method = method;
        context.Request.Body = new MemoryStream(method == "POST" ? Encoding.UTF8.GetBytes("""{"model":"gpt-5"}""") : []);
        var (status, input, output, model, _) = await proxy.ForwardAsync(context, path, Ct);

        var sent = Assert.Single(upstream.Seen);
        Assert.Equal((method, $"https://api.business.githubcopilot.example/{path}"), (sent.Method.Method, sent.RequestUri!.ToString()));
        Assert.Equal("Bearer github-token", sent.Headers.Authorization!.ToString());
        Assert.Equal(200, status);
        if (method == "POST")
            Assert.Equal((21L, 8L, "gpt-5"), (input, output, model));
    }

    /// <summary>
    /// LOG 496: Copilot CLI accepts only a GitHub-prefixed token, so its step token arrives as <c>ghs_</c> and the token.
    /// The model route drops the prefix, and a token without it still works; the engine tools and hooks take no prefix.
    /// </summary>
    [Fact]
    public async Task The_model_route_accepts_a_ghs_prefixed_step_token_and_the_other_routes_do_not()
    {
        await StepAsync();
        var upstream = new Upstream(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var proxied = services with
        {
            Proxies = new Dictionary<string, ModelProxy>
            {
                ["claude"] = new(new ModelProxyOptions(new Uri("https://model.example/"), "github", _ => Task.FromResult<string?>("k")), upstream),
            },
        };
        await using var app = AgentEndpoint.Build(proxied, "http://127.0.0.1:0");
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var token = proxied.Tokens.Issue(Claims with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });

        async Task<HttpStatusCode> Send(string path, string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("""{"model":"m"}""", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Send("/model/v1/messages", "ghs_" + token));
        Assert.Equal(HttpStatusCode.OK, await Send("/model/v1/messages", token));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send("/model/v1/messages", "ghs_ghs_" + token));
        Assert.Equal(HttpStatusCode.Unauthorized, await Send("/agent/v1/report", "ghs_" + token));
        Assert.Equal(2, upstream.Seen.Count);
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
    public async Task With_the_model_unreachable_the_proxy_answers_502_with_the_cause_and_never_the_credential()
    {
        var upstream = new Upstream(_ => throw new HttpRequestException("The SSL connection could not be established.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid: UntrustedRoot")));
        var proxy = new ModelProxy(new ModelProxyOptions(new Uri("https://model.example/"), "api_key", _ => Task.FromResult<string?>("real-credential")), upstream);
        var context = Request("Bearer step-token");
        var (status, _, _, _, unreachable) = await proxy.ForwardAsync(context, "v1/messages", Ct);
        Assert.Equal(502, status);
        context.Response.Body.Position = 0;
        var body = System.Text.Json.Nodes.JsonNode.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync(Ct))!;
        Assert.Equal("the model proxy could not reach the model: The SSL connection could not be established. The remote certificate is invalid: UntrustedRoot",
            body["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("real-credential", body.ToJsonString());
        Assert.Equal("The SSL connection could not be established. The remote certificate is invalid: UntrustedRoot", unreachable);
    }

    [Fact]
    public async Task A_model_unreachable_is_signalled_to_the_run_once_per_step_and_cause()
    {
        var other = Claims with { StepKey = "run-1/review/1", StepId = "review" };
        foreach (var (claims, cause) in new[] { (Claims, "refused"), (Claims, "refused"), (Claims, "UntrustedRoot"), (Claims, "refused"), (other, "refused") })
            await AgentEndpoint.ModelUnreachableAsync(services, claims, cause, Ct);

        Assert.Equal([("run-1", new ModelUnreachableSignal("implement", "refused")), ("run-1", new ModelUnreachableSignal("implement", "UntrustedRoot")),
            ("run-1", new ModelUnreachableSignal("review", "refused"))], signals.Unreachable);
    }
}
