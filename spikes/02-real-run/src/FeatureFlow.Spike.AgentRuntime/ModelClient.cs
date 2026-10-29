using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// One call to a chat-completions model, and its answer.
/// </summary>
public sealed record ModelRequest(
    string SystemPrompt,
    string UserPrompt,
    double Temperature = 0.0,
    int MaxTokens = 2048);

public sealed record ModelResponse(
    string Text,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    double? CostUsd)
{
    /// <summary>Whether the answer carried any text at all.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// A minimal OpenAI-compatible chat-completions client.
///
/// <para>
/// This exists because review 14 A7 and A8 need a <b>model</b> to author a
/// verdict and a patch, and the spike previously had none: <c>review.run</c>
/// derived its verdict from the diff hash, and the <c>insert_after</c>
/// measurement generated its patches in code. Both were circular.
/// </para>
///
/// <para>
/// The credential is resolved through the secrets provider by reference and
/// registered with the scrubber, so no key value reaches a log or a record
/// (floor F2, docs/01 4.8). The endpoint and model are configuration, never
/// template content (docs/09 11.1).
/// </para>
/// </summary>
public sealed class ModelClient
{
    private readonly HttpClient http;
    private readonly ISecretsProvider secrets;
    private readonly SecretScrubber scrubber;
    private readonly SecretReference keyRef;
    private readonly string endpoint;
    private readonly string model;

    public ModelClient(
        string endpoint,
        string model,
        ISecretsProvider secrets,
        SecretReference? keyRef = null,
        SecretScrubber? scrubber = null,
        HttpClient? http = null)
    {
        this.endpoint = endpoint.TrimEnd('/');
        this.model = model;
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.keyRef = keyRef ?? new SecretReference("secrets", "model-api-key");
        this.scrubber = scrubber ?? new SecretScrubber();
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>The endpoint this client calls. Safe to log.</summary>
    public string Endpoint => endpoint;

    /// <summary>The model this client asks for. Safe to log.</summary>
    public string Model => model;

    public async Task<ProviderResult<ModelResponse>> CompleteAsync(
        ModelRequest request, CancellationToken ct)
    {
        var key = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (!key.IsOk)
            return ProviderResult<ModelResponse>.Fail(key.Error!);

        scrubber.Register(key.Value!.Value);

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt },
            },
        };

        using var content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json");
        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post, endpoint + "/chat/completions") { Content = content };
        requestMessage.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", key.Value!.Value);

        try
        {
            using var response = await http.SendAsync(requestMessage, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Classified by status code, never by matching the body's text
                // (docs/04 section 1).
                var status = (int)response.StatusCode;
                var errorClass = status is 408 or 429 || status >= 500
                    ? ErrorClass.Infrastructure
                    : ErrorClass.Work;

                return ProviderResult<ModelResponse>.Fail(new ProviderError(
                    errorClass,
                    "model_call_failed",
                    RetryAfter(status),
                    null,
                    scrubber.Scrub($"HTTP {status}: {Truncate(text)}")));
            }

            return Parse(text);
        }
        catch (TaskCanceledException)
        {
            return ProviderResult<ModelResponse>.Infrastructure("timeout");
        }
        catch (HttpRequestException ex)
        {
            return ProviderResult<ModelResponse>.Infrastructure(
                "unreachable", TimeSpan.FromSeconds(5), scrubber.Scrub(ex.Message));
        }
    }

    private ProviderResult<ModelResponse> Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var content = root.TryGetProperty("choices", out var choices) &&
                          choices.GetArrayLength() > 0 &&
                          choices[0].TryGetProperty("message", out var message) &&
                          message.TryGetProperty("content", out var value)
                ? value.GetString() ?? ""
                : "";

            string? reportedModel = null;
            if (root.TryGetProperty("model", out var modelName))
                reportedModel = modelName.GetString();

            long? input = null;
            long? output = null;
            double? cost = null;

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var i))
                    input = i;
                if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt64(out var o))
                    output = o;
                if (usage.TryGetProperty("cost", out var spent) && spent.TryGetDouble(out var c))
                    cost = c;
            }

            return ProviderResult<ModelResponse>.Ok(new ModelResponse(
                content, reportedModel ?? model, input, output, cost));
        }
        catch (JsonException ex)
        {
            return ProviderResult<ModelResponse>.Infrastructure(
                "malformed_response", null, scrubber.Scrub(ex.Message));
        }
    }

    private static TimeSpan? RetryAfter(int status) =>
        status is 408 or 429 or 502 or 503 or 504 ? TimeSpan.FromSeconds(5) : null;

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";
}
