using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// Mints the GitHub App JWT: RS256, `iss` = app id, `iat` = now-60,
/// `exp` = now+540 (GitHub's 10 minute maximum).
///
/// Uses .NET's built-in <see cref="RSA"/>, so no new package dependency. The
/// PEM is parsed here rather than by a library, which is the only fiddly part:
/// GitHub issues RSA private keys in PKCS#1 ("BEGIN RSA PRIVATE KEY"), which
/// `RSA.ImportRSAPrivateKey` reads directly, but PKCS#8 ("BEGIN PRIVATE KEY")
/// and encrypted PKCS#8 are also possible, so all three are handled.
/// </summary>
public static class GitHubAppJwt
{
    /// <summary>GitHub's maximum JWT lifetime is 10 minutes.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Clock skew allowance, per GitHub's own guidance.</summary>
    private static readonly TimeSpan Backdate = TimeSpan.FromSeconds(60);

    public static string Mint(string appId, string privateKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("app id is required", nameof(appId));

        var issuedAt = now - Backdate;
        var expiresAt = now + MaxLifetime - Backdate;

        var header = Base64Url(Encoding.UTF8.GetBytes(
            """{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, long>
        {
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["iss"] = long.Parse(appId, CultureInfo.InvariantCulture),
        })));

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");

        using var rsa = ImportRsaPrivateKey(privateKeyPem);
        var signature = rsa.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    /// <summary>
    /// Imports an RSA private key from PEM text. Handles PKCS#1, PKCS#8 and
    /// encrypted PKCS#8.
    /// </summary>
    public static RSA ImportRsaPrivateKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            throw new ArgumentException("private key PEM is empty", nameof(pem));

        var label = PemLabel(pem);
        var der = Convert.FromBase64String(PemBody(pem));

        var rsa = RSA.Create();

        try
        {
            switch (label)
            {
                case "RSA PRIVATE KEY":
                    rsa.ImportRSAPrivateKey(der, out _);
                    break;

                case "PRIVATE KEY":
                    rsa.ImportPkcs8PrivateKey(der, out _);
                    break;

                case "ENCRYPTED PRIVATE KEY":
                    // An encrypted key needs its passphrase, which the spike does
                    // not have. Fail with a message that says what to do.
                    rsa.Dispose();
                    throw new InvalidOperationException(
                        "the GitHub App private key is encrypted; re-download it " +
                        "unencrypted, or supply the passphrase");

                default:
                    rsa.Dispose();
                    throw new InvalidOperationException(
                        $"unsupported private key PEM type '{label}'");
            }
        }
        catch
        {
            rsa.Dispose();
            throw;
        }

        return rsa;
    }

    private static string PemLabel(string pem)
    {
        var begin = pem.IndexOf("-----BEGIN", StringComparison.Ordinal);
        if (begin < 0)
            throw new InvalidOperationException("private key is not PEM: no BEGIN header");

        var end = pem.IndexOf("-----", begin + 10, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("private key PEM header is malformed");

        return pem[(begin + 10)..end].Trim();
    }

    private static string PemBody(string pem)
    {
        var sb = new StringBuilder();
        foreach (var raw in pem.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.StartsWith("-----", StringComparison.Ordinal))
                continue;

            sb.Append(line);
        }

        if (sb.Length == 0)
            throw new InvalidOperationException("private key PEM has no body");

        return sb.ToString();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
