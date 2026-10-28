using System.Net;

namespace FeatureFlow.Spike.CodeHostHarness;

/// <summary>
/// A transport that fails on demand, so the infrastructure classification can be
/// exercised through the real provider code path rather than a stub.
///
/// <para>
/// Two failure modes matter and they must be distinguishable:
/// </para>
/// <list type="bullet">
/// <item><see cref="FailureMode.Connection"/> throws
/// <see cref="HttpRequestException"/>, which is what a DNS failure or a refused
/// connection looks like. Classified <c>unreachable</c>.</item>
/// <item><see cref="FailureMode.Timeout"/> throws
/// <see cref="TaskCanceledException"/> without the caller's token being
/// cancelled, which is how <see cref="HttpClient"/> reports its own timeout.
/// Classified <c>timeout</c>.</item>
/// </list>
/// </summary>
public enum FailureMode
{
    None,
    Connection,
    Timeout,
    ServerError,
    RateLimited,
}

public sealed class FailingHandler : HttpMessageHandler
{
    public FailureMode Mode { get; set; } = FailureMode.None;

    /// <summary>What <c>Retry-After</c> should say on a 429.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (Mode)
        {
            case FailureMode.Connection:
                throw new HttpRequestException("simulated connection failure");

            case FailureMode.Timeout:
                // HttpClient surfaces its own timeout as TaskCanceledException
                // with the caller's token untouched. Reproduce that exactly.
                throw new TaskCanceledException("simulated timeout");

            case FailureMode.ServerError:
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"message":"simulated server error"}"""),
                });

            case FailureMode.RateLimited:
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"message":"simulated rate limit"}"""),
                };
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter);
                return Task.FromResult(response);
        }

        throw new InvalidOperationException($"unrouted request: {request.Method} {request.RequestUri}");
    }
}
