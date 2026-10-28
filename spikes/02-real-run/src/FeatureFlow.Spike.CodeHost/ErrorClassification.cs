using System.Net;
using System.Net.Http.Headers;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// Maps transport outcomes to <see cref="ErrorClass"/>. docs/05 section 1.3 and
/// docs/01 section 4.3.
///
/// <para>
/// This is the seam Spike 1 left open. Spike 1 classified by string-matching an
/// exception message, which breaks the moment a provider rewords an error. Here
/// classification is by <b>HTTP status code and exception type</b>, both of
/// which are stable contracts of the transport, not of the message text.
/// </para>
///
/// <para>
/// The distinction that matters: <see cref="ErrorClass.Infrastructure"/> means
/// the work did not happen, so retrying is safe and does not consume an
/// attempt. <see cref="ErrorClass.Work"/> means the remote system answered and
/// the answer was no, so retrying the same request gets the same answer and
/// consumes an attempt.
/// </para>
/// </summary>
public static class ErrorClassification
{
    /// <summary>
    /// Classifies an HTTP status code. <paramref name="operation"/> is used
    /// only to distinguish a 403 on merge (branch protection, a work outcome)
    /// from a 403 anywhere else (a configuration fault, infrastructure).
    /// </summary>
    public static ProviderError FromStatus(HttpStatusCode status, string operation, HttpResponseHeaders? headers = null)
    {
        var code = (int)status;

        if (code >= 500)
            return new ProviderError(ErrorClass.Infrastructure, "server_error");

        return code switch
        {
            401 => new ProviderError(ErrorClass.Infrastructure, "unauthenticated"),

            // A 403 on merge is branch protection: a human must approve on the
            // code host and the engine waits (Q5). That is a work outcome, not
            // a configuration fault. Everywhere else a 403 is a fault.
            403 when operation == "merge" =>
                new ProviderError(ErrorClass.Work, "rejected"),
            403 => new ProviderError(ErrorClass.Infrastructure, "forbidden"),

            404 => new ProviderError(ErrorClass.Work, "not_found"),
            409 => new ProviderError(ErrorClass.Work, "conflict"),
            422 => new ProviderError(ErrorClass.Work, "validation_failed"),

            429 => new ProviderError(
                ErrorClass.Infrastructure,
                "rate_limited",
                ParseRetryAfter(headers)),

            _ => new ProviderError(ErrorClass.Infrastructure, "server_error"),
        };
    }

    /// <summary>
    /// Classifies an exception that escaped the transport. Type-based, never
    /// message-based: a timeout is infrastructure whether the message says
    /// "timed out", "deadline exceeded" or nothing at all.
    /// </summary>
    public static ProviderError FromException(Exception ex)
    {
        // TaskCanceledException is how HttpClient reports both a timeout and a
        // genuine cancellation. Only the timeout case is an error; a cancelled
        // token is the caller's own doing and must not be swallowed into a
        // classified result.
        if (ex is OperationCanceledException)
            return new ProviderError(ErrorClass.Infrastructure, "timeout");

        if (ex is HttpRequestException)
            return new ProviderError(ErrorClass.Infrastructure, "unreachable");

        // Anything else is a bug in the provider. The engine's decorator
        // normally sets this; classifying it here too keeps the provider
        // honest when called directly, as the harness does.
        return new ProviderError(ErrorClass.Infrastructure, "internal_error");
    }

    /// <summary>
    /// Reads <c>Retry-After</c>. GitHub sends it as a whole number of seconds
    /// on 429 and 403 secondary-rate-limit responses; the HTTP-date form is
    /// accepted too because the header allows it.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseHeaders? headers)
    {
        if (headers?.RetryAfter is null)
            return null;

        var retryAfter = headers.RetryAfter;

        if (retryAfter.Delta.HasValue)
            return retryAfter.Delta.Value;

        if (retryAfter.Date.HasValue)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
