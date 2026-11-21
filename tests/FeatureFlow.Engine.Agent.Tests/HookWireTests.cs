using System.Net;
using System.Text;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.SandboxClient;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// The hook endpoint's answer as the sandbox client reads it: a <see cref="HookDecision"/> serialised as the endpoint does,
/// applied by the hook command, so a change of the wire shape on either side fails here rather than falling back to exit 2.
/// </summary>
public sealed class HookWireTests
{
    sealed class Endpoint(HookDecision decision) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(decision, AgentEndpoint.Json), Encoding.UTF8, "application/json"),
            });
    }

    public static TheoryData<HookDecision, int, string, string> Decisions => new()
    {
        { HookDecision.Allow, 0, "", "" },
        // Copilot's denial: its JSON decision on standard output, exit 0 (LOG 293).
        { HookDecision.Deny("never push") with { Reply = new HookReply(0, Output: """{"permissionDecision":"deny","permissionDecisionReason":"never push"}""") },
            0, """{"permissionDecision":"deny","permissionDecisionReason":"never push"}""", "" },
        // Claude Code's denial: exit 2, the reason on standard error.
        { HookDecision.Deny("never push") with { Reply = new HookReply(2, Error: "never push") }, 2, "", "never push" },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task The_hook_command_applies_the_endpoint_s_decision_as_serialised(HookDecision decision, int exit, string printed, string shown)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var client = new EngineClient(["http://replica"], "token", TimeSpan.FromSeconds(5), new ClientLog(null), new Endpoint(decision));

        Assert.Equal(exit, await HookCommand.RunAsync("pre_tool_use", client, new StringReader("""{"tool_name":"Bash"}"""), output, error, new ClientLog(null),
            TestContext.Current.CancellationToken));
        Assert.Equal(printed, output.ToString().Trim());
        Assert.Equal(shown, error.ToString().Trim());
    }
}
