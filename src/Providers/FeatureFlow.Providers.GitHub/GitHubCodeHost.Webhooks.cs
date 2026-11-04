using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// GitHub's webhooks for the inbound gateway (docs/02 section 2.3): a delivery is verified by its
/// <c>X-Hub-Signature-256</c>, the hex HMAC-SHA256 of the body under the webhook's secret, compared in constant time
/// (https://docs.github.com/webhooks/using-webhooks/validating-webhook-deliveries). A verified event about something a
/// wait reads (a pull request and its reviews, checks, statuses, workflow runs, issues, pushes) names its repository;
/// any other names none and wakes nothing.
/// </summary>
public sealed partial class GitHubCodeHost : IInboundEventSource
{
    /// <summary>The events that may change what a wait reads: <c>scm.merge</c>, <c>ci.checks</c> and <c>wait.condition</c>.</summary>
    public static readonly IReadOnlySet<string> WakingEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "pull_request", "pull_request_review", "check_run", "check_suite", "status", "workflow_run", "issues", "push",
    };

    /// <summary>The webhook's secret, from the secrets provider (<c>webhook_secret_ref</c>); none, and no delivery verifies.</summary>
    public Func<CancellationToken, Task<ProviderResult<string>>>? WebhookSecret { get; init; }

    public string Source => "github";

    public async Task<ProviderResult<InboundEvent>> ReadEventAsync(InboundRequest request, CancellationToken cancellationToken)
    {
        if (WebhookSecret is null)
            return ProviderResult<InboundEvent>.Failure("unauthenticated", "no webhook_secret_ref is configured for the GitHub code host: no delivery can be verified");
        if (request.Header("X-Hub-Signature-256") is not { } signature)
            return ProviderResult<InboundEvent>.Failure("unauthenticated", "the delivery is not signed (no X-Hub-Signature-256)");
        var secret = await WebhookSecret(cancellationToken);
        if (!secret.Ok)
            return new ProviderResult<InboundEvent>(null, secret.Error);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret.Value!, request.Body)), Encoding.ASCII.GetBytes(signature)))
            return ProviderResult<InboundEvent>.Failure("unauthenticated", "the delivery's signature does not verify");
        if (request.Header("X-GitHub-Event") is not { Length: > 0 } name || request.Header("X-GitHub-Delivery") is not { Length: > 0 } delivery)
            return ProviderResult<InboundEvent>.Failure("validation_failed", "the delivery has no X-GitHub-Event or X-GitHub-Delivery");
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(request.Body) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
            return ProviderResult<InboundEvent>.Failure("validation_failed", "the delivery's body is not JSON: set the webhook's content type to application/json");
        var action = body["action"]?.GetValueKind() == JsonValueKind.String ? body["action"]!.GetValue<string>() : null;
        var repository = body["repository"]?["full_name"]?.GetValueKind() == JsonValueKind.String ? body["repository"]!["full_name"]!.GetValue<string>() : null;
        return ProviderResult<InboundEvent>.Success(new InboundEvent(Source, delivery, action is null ? name : $"{name}.{action}",
            WakingEvents.Contains(name) ? repository : null));
    }

    /// <summary>GitHub's delivery signature: <c>sha256=</c> and the lower-case hex HMAC-SHA256 of the body.</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
}
