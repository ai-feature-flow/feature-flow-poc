using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>GitHub's webhook deliveries for the inbound gateway: verified by their signature before anything is read from them.</summary>
public sealed class GitHubWebhookTests
{
    const string Secret = "webhook-secret";
    const string Body = """{ "action": "completed", "check_run": { "head_sha": "abc" }, "repository": { "full_name": "o/r" } }""";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static GitHubCodeHost Host(string? secret = Secret) => new(new GitHubApi(() => null)) { WebhookSecret = secret is null ? null : _ => Task.FromResult(ProviderResult<string>.Success(secret)) };

    static InboundRequest Delivery(string body = Body, string @event = "check_run", string? signature = null, string delivery = "d-1")
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = @event, ["X-GitHub-Delivery"] = delivery };
        headers["x-hub-signature-256"] = signature ?? GitHubCodeHost.Sign(Secret, body);
        return new InboundRequest(headers, body, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_signed_delivery_is_its_event_its_delivery_id_and_its_repository()
    {
        var read = await Host().ReadEventAsync(Delivery(), Ct);

        Assert.True(read.Ok, read.Error?.Message);
        Assert.Equal(new InboundEvent("github", "d-1", "check_run.completed", "o/r"), read.Value);
    }

    [Fact]
    public async Task A_delivery_signed_with_another_secret_or_changed_after_signing_or_unsigned_is_unauthenticated()
    {
        var host = Host();
        var tampered = Delivery() with { Body = Body.Replace("o/r", "o/other", StringComparison.Ordinal) };
        var unsigned = new InboundRequest(new Dictionary<string, string> { ["X-GitHub-Event"] = "push", ["X-GitHub-Delivery"] = "d" }, Body, DateTimeOffset.UtcNow);

        foreach (var request in new[] { Delivery(signature: GitHubCodeHost.Sign("guess", Body)), tampered, unsigned, Delivery(signature: "sha256=") })
            Assert.Equal("unauthenticated", (await host.ReadEventAsync(request, Ct)).Error?.Reason);
        // With no secret configured, nothing verifies, however it is signed.
        Assert.Equal("unauthenticated", (await Host(secret: null).ReadEventAsync(Delivery(), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task An_event_no_wait_reads_names_no_repository_and_a_malformed_delivery_is_invalid()
    {
        var host = Host();

        var star = await host.ReadEventAsync(Delivery(@event: "star"), Ct);
        Assert.Equal(("star.completed", null), (star.Value!.Event, star.Value.Repository));
        Assert.Equal("validation_failed", (await host.ReadEventAsync(Delivery(body: "payload=x"), Ct)).Error?.Reason);
        Assert.Equal("validation_failed", (await host.ReadEventAsync(Delivery(delivery: ""), Ct)).Error?.Reason);
    }
}
