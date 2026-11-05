using System.Security.Cryptography;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace FeatureFlow.Engine;

/// <summary>
/// The <c>aes_gcm</c> payload codec (docs/02 section 5, docs/07 section 2): every payload the engine sends Temporal is
/// encrypted with AES-256-GCM under the installation's key, so workflow histories hold no readable inputs, outputs or
/// records. A payload becomes <c>binary/encrypted</c> holding a fresh 12-byte nonce, the ciphertext of the whole original
/// payload and the 16-byte tag; a tampered payload or a wrong key fails to decode. A payload not encoded by this codec is
/// passed through on decode. <see cref="DataConverterFor"/> also moves failure messages and stack traces into encoded
/// attributes, so they are encrypted too.
/// </summary>
public sealed class AesGcmPayloadCodec : IPayloadCodec
{
    public const string Encoding = "binary/encrypted";
    const int NonceSize = 12, TagSize = 16;
    static readonly ByteString EncodingBytes = ByteString.CopyFromUtf8(Encoding);

    readonly byte[] key;

    public AesGcmPayloadCodec(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("the aes_gcm payload key must be 32 bytes");
        this.key = key;
    }

    /// <summary>The key from its secret: 32 bytes, base64-encoded (<c>openssl rand -base64 32</c>).</summary>
    public static AesGcmPayloadCodec FromSecret(string secret)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("temporal.payload_codec: the key secret is not base64; make one with 'openssl rand -base64 32'");
        }

        return key.Length == 32
            ? new AesGcmPayloadCodec(key)
            : throw new InvalidOperationException($"temporal.payload_codec: the key secret holds {key.Length} bytes; aes_gcm needs 32 ('openssl rand -base64 32')");
    }

    /// <summary>The default converter with this codec, and failure messages and stack traces encoded with it.</summary>
    public static DataConverter DataConverterFor(IPayloadCodec codec) =>
        DataConverter.Default with
        {
            PayloadCodec = codec,
            FailureConverter = new DefaultFailureConverter.WithEncodedCommonAttributes(),
        };

    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult<IReadOnlyCollection<Payload>>(payloads.Select(Encrypt).ToList());

    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads) =>
        Task.FromResult<IReadOnlyCollection<Payload>>(payloads.Select(p => p.Metadata.TryGetValue("encoding", out var e) && e == EncodingBytes ? Decrypt(p) : p).ToList());

    Payload Encrypt(Payload payload)
    {
        var plain = payload.ToByteArray();
        var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length));
        return new Payload { Metadata = { ["encoding"] = EncodingBytes }, Data = ByteString.CopyFrom(sealedBytes) };
    }

    Payload Decrypt(Payload payload)
    {
        var sealedBytes = payload.Data.Span;
        if (sealedBytes.Length < NonceSize + TagSize)
            throw new CryptographicException("an encrypted payload is too short");
        var plain = new byte[sealedBytes.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes[..NonceSize], sealedBytes.Slice(NonceSize, plain.Length), sealedBytes[(NonceSize + plain.Length)..], plain);
        return Payload.Parser.ParseFrom(plain);
    }
}
