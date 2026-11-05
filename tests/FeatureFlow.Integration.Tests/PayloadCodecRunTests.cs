using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Engine;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// The payload codec in a whole run (docs/02 section 5, docs/07 section 2): with <c>temporal.payload_codec</c> set, the
/// engine's Temporal histories hold no plaintext, and replay needs the key. The control runs the same ticket without the
/// codec and finds the plaintext with the same check.
/// </summary>
[Trait("Category", "Integration")]
public class PayloadCodecRunTests
{
    const string Script = """{ "ticket_delivery:implement": [{ error: work }, implemented] }""";

    [Fact]
    public async Task With_the_codec_a_runs_histories_hold_no_plaintext_and_replay_only_with_its_key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var marker = "FF-" + Guid.NewGuid().ToString("N")[..10];
        await using var h = await Harness.StartAsync("codec", Script, autoApprove: true,
            temporal: ", payload_codec: { name: aes_gcm, key_ref: secrets/payload_key }",
            secrets: new Dictionary<string, string> { ["payload_key"] = Convert.ToBase64String(key) });

        var id = await RunAsync(h, marker);
        var histories = await HistoriesAsync(h.Client, id);

        foreach (var history in histories)
        {
            var payloads = Payloads(JsonNode.Parse(history.ToJson())!).ToList();
            Assert.NotEmpty(payloads);
            Assert.All(payloads, p => Assert.Equal(AesGcmPayloadCodec.Encoding, p.Encoding));
            Assert.DoesNotContain(payloads, p => p.Text.Contains(marker, StringComparison.Ordinal));
        }

        var first = histories[0];
        var withKey = await new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DebugMode = true, DataConverter = AesGcmPayloadCodec.DataConverterFor(new AesGcmPayloadCodec(key)),
        }.AddWorkflow<RunWorkflow>()).ReplayWorkflowAsync(first, throwOnReplayFailure: false, TestContext.Current.CancellationToken);
        Assert.True(withKey.ReplayFailure is null, $"does not replay with the key: {withKey.ReplayFailure}");
        var withoutKey = await new WorkflowReplayer(new WorkflowReplayerOptions { DebugMode = true }.AddWorkflow<RunWorkflow>())
            .ReplayWorkflowAsync(first, throwOnReplayFailure: false, TestContext.Current.CancellationToken);
        Assert.NotNull(withoutKey.ReplayFailure);
    }

    [Fact]
    public async Task Without_the_codec_the_same_check_finds_the_plaintext()
    {
        var marker = "FF-" + Guid.NewGuid().ToString("N")[..10];
        await using var h = await Harness.StartAsync("nocodec", Script, autoApprove: true);

        var id = await RunAsync(h, marker);

        var payloads = (await HistoriesAsync(h.Client, id)).SelectMany(x => Payloads(JsonNode.Parse(x.ToJson())!)).ToList();
        Assert.DoesNotContain(payloads, p => p.Encoding == AesGcmPayloadCodec.Encoding);
        Assert.Contains(payloads, p => p.Text.Contains(marker, StringComparison.Ordinal));
    }

    static async Task<string> RunAsync(Harness h, string marker)
    {
        var id = h.Start("std:templates/ticket_delivery", "--param", $"ticket_id={marker}", "--repo", "acme/api");
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");
        Assert.True(exit == 0 && end.Contains("completed", StringComparison.Ordinal), end + error);
        Assert.Equal(2, Harness.StartedSteps(await h.ShowAsync(id)).Count(s => s == "implement"));
        return id;
    }

    static async Task<List<WorkflowHistory>> HistoriesAsync(ITemporalClient client, string workflowId)
    {
        var list = new List<WorkflowHistory>();
        await foreach (var execution in client.ListWorkflowsAsync($"WorkflowId = '{workflowId}'"))
            list.Add(await client.GetWorkflowHandle(workflowId, execution.RunId).FetchHistoryAsync());
        return list;
    }

    /// <summary>Every payload in a history's JSON, by its <c>metadata.encoding</c> and its data as text.</summary>
    static IEnumerable<(string Encoding, string Text)> Payloads(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o when o["metadata"] is JsonObject { } metadata && metadata["encoding"] is JsonValue encoding:
                yield return (Text(encoding), o["data"] is JsonValue data ? Text(data) : "");
                break;
            case JsonObject o:
                foreach (var (_, child) in o)
                    foreach (var p in Payloads(child))
                        yield return p;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var p in Payloads(child))
                        yield return p;
                break;
        }

        static string Text(JsonValue base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64.GetValue<string>()));
    }
}
