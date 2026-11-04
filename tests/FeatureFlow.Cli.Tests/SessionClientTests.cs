using System.Net;
using System.Text;
using FeatureFlow.Cli;
using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Cli.Tests;

public class SessionClientTests
{
    const string Sid = "run-1.wait-1";
    static readonly string Commit = new('a', 40);

    /// <summary>Refuses the first <paramref name="refusals"/> check requests with <paramref name="refusal"/>, then starts the check, which passes.</summary>
    sealed class FakeApi(int refusals, string refusal) : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                return Task.FromResult(Posts <= refusals
                    ? Reply(HttpStatusCode.Conflict, $$"""{"error":"{{refusal}}"}""")
                    : Reply(HttpStatusCode.OK, """{"check_id":"c1"}"""));
            }

            return Task.FromResult(Reply(HttpStatusCode.OK, """{"checks":[{"check":{"checkId":"c1","status":"passed"},"detail":"valid"}]}"""));
        }

        static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    static SessionClient Client(FakeApi api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("http://api.test/") }) { RetryInterval = TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task A_check_refused_while_the_run_hands_over_is_tried_again()
    {
        var api = new FakeApi(3, SessionNames.HandingOver);

        var (exit, text) = await Client(api).CheckAsync(Sid, "check_requirements", Commit, TimeSpan.FromSeconds(10));

        Assert.True(exit == 0, text);
        Assert.Equal(4, api.Posts);
    }

    [Fact]
    public async Task Any_other_refusal_of_a_check_is_shown_at_once()
    {
        var api = new FakeApi(1, "check_requirements is not a check of session run-1.wait-1");

        var (exit, text) = await Client(api).CheckAsync(Sid, "check_requirements", Commit, TimeSpan.FromSeconds(10));

        Assert.Equal(1, exit);
        Assert.Contains("409: check_requirements is not a check", text);
        Assert.Equal(1, api.Posts);
    }
}
