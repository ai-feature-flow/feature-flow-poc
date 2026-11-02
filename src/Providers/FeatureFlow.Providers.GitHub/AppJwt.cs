using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub App's JWT: RS256, <c>iss</c> the App id, issued 60 seconds in the past and valid for nine minutes
/// (GitHub allows ten). Ported from Spike 2 (<c>spikes/02-real-run</c>, <c>GitHubAppJwt</c>).
/// </summary>
public static class AppJwt
{
    public static string Mint(string appId, string privateKeyPem, DateTimeOffset now)
    {
        var issuedAt = now - TimeSpan.FromSeconds(60);
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, long>
        {
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = (issuedAt + TimeSpan.FromMinutes(9)).ToUnixTimeSeconds(),
            ["iss"] = long.Parse(appId, CultureInfo.InvariantCulture),
        })));
        using var rsa = ImportRsa(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    /// <summary>Reads PKCS#1 (GitHub's format) or PKCS#8 PEM. An encrypted key is refused with a message saying what to do.</summary>
    public static RSA ImportRsa(string pem)
    {
        if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException("the GitHub App private key is encrypted; give an unencrypted key");
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
