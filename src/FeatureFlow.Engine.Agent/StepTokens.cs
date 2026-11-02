using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// What a per-step token carries (docs/02 section 4.2): the step, its run and workflow, and the policy a replica
/// applies to a hook call from the token alone: the role's write access and tool classes. It expires.
/// </summary>
public sealed record StepClaims(
    [property: JsonPropertyName("k")] string StepKey,
    [property: JsonPropertyName("r")] string RunId,
    [property: JsonPropertyName("w")] string WorkflowId,
    [property: JsonPropertyName("s")] string StepId,
    [property: JsonPropertyName("a")] string WriteAccess,
    [property: JsonPropertyName("t")] IReadOnlyList<string> Tools,
    [property: JsonPropertyName("e")] long ExpiresAt);

/// <summary>
/// Signs and checks per-step tokens with HMAC-SHA256 under a key every agent worker replica shares, so any replica
/// serves any step (RW17). A token is <c>ffs1.&lt;claims&gt;.&lt;signature&gt;</c>, both base64url. It is worthless
/// outside the engine: it authorises only this step's calls to the hook endpoint, engine tools and model proxy.
/// </summary>
public sealed class StepTokens(byte[] key, Func<DateTimeOffset>? clock = null)
{
    const string Prefix = "ffs1.";
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public static StepTokens FromSecret(string secret) => new(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public string Issue(StepClaims claims)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        return $"{Prefix}{body}.{Base64Url(Sign(body))}";
    }

    /// <summary>The claims of a valid, unexpired token, or null.</summary>
    public StepClaims? Check(string? token)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = token[Prefix.Length..].Split('.');
        if (parts.Length != 2 || FromBase64Url(parts[1]) is not { } signature)
            return null;
        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
            return null;
        try
        {
            var claims = JsonSerializer.Deserialize<StepClaims>(FromBase64Url(parts[0]) ?? []);
            return claims is not null && claims.ExpiresAt > now().ToUnixTimeSeconds() ? claims : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    byte[] Sign(string body) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[]? FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
