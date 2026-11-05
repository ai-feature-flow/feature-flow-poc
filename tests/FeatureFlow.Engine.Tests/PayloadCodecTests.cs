using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace FeatureFlow.Engine.Tests;

/// <summary>The <c>aes_gcm</c> payload codec on its own: round trip, no plaintext, tampering and a wrong key refused.</summary>
public class PayloadCodecTests
{
    static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    const string Secret = "run FF-4242 for acme/api: the token is hunter2";

    static Payload Plain(string text) => DataConverter.Default.PayloadConverter.ToPayload(text);

    [Fact]
    public async Task A_payload_round_trips_and_its_encoded_form_holds_none_of_it()
    {
        var codec = new AesGcmPayloadCodec(Key);
        var original = Plain(Secret);

        var encoded = Assert.Single(await codec.EncodeAsync([original]));
        var decoded = Assert.Single(await codec.DecodeAsync([encoded]));

        Assert.Equal(original, decoded);
        Assert.Equal(AesGcmPayloadCodec.Encoding, encoded.Metadata["encoding"].ToStringUtf8());
        Assert.Single(encoded.Metadata);
        var wire = encoded.ToByteArray();
        Assert.Equal(-1, wire.AsSpan().IndexOf(Encoding.UTF8.GetBytes("FF-4242")));
        Assert.Equal(-1, wire.AsSpan().IndexOf(Encoding.UTF8.GetBytes("json/plain")));
        // A fresh nonce each time: the same payload never encrypts the same way twice.
        Assert.NotEqual(encoded.Data, Assert.Single(await codec.EncodeAsync([original])).Data);
    }

    [Fact]
    public async Task A_tampered_payload_or_another_key_does_not_decode()
    {
        var encoded = Assert.Single(await new AesGcmPayloadCodec(Key).EncodeAsync([Plain(Secret)]));
        var bytes = encoded.Data.ToByteArray();
        bytes[^20] ^= 1;
        var tampered = new Payload { Metadata = { ["encoding"] = encoded.Metadata["encoding"] }, Data = ByteString.CopyFrom(bytes) };

        await Assert.ThrowsAnyAsync<CryptographicException>(() => new AesGcmPayloadCodec(Key).DecodeAsync([tampered]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => new AesGcmPayloadCodec(RandomNumberGenerator.GetBytes(32)).DecodeAsync([encoded]));
    }

    [Fact]
    public async Task A_payload_the_codec_did_not_encode_passes_through()
    {
        var plain = Plain("not encrypted");
        Assert.Same(plain, Assert.Single(await new AesGcmPayloadCodec(Key).DecodeAsync([plain])));
    }

    [Theory]
    [InlineData("not base64!", "not base64")]
    [InlineData("c2hvcnQ=", "holds 5 bytes")]
    public void The_key_secret_is_32_bytes_of_base64(string secret, string message)
    {
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => AesGcmPayloadCodec.FromSecret(secret)).Message);
        Assert.NotNull(AesGcmPayloadCodec.FromSecret(Convert.ToBase64String(Key) + "\n"));
    }

    [Fact]
    public async Task The_data_converter_encrypts_failure_messages_too()
    {
        var converter = AesGcmPayloadCodec.DataConverterFor(new AesGcmPayloadCodec(Key));
        var failure = converter.FailureConverter.ToFailure(new InvalidOperationException(Secret), converter.PayloadConverter);
        await converter.PayloadCodec!.EncodeFailureAsync(failure);

        Assert.DoesNotContain("FF-4242", failure.ToString());
        Assert.Equal(AesGcmPayloadCodec.Encoding, failure.EncodedAttributes.Metadata["encoding"].ToStringUtf8());
    }
}
