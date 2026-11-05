using FeatureFlow.Engine;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Client;

namespace FeatureFlow.Hosting;

/// <summary>
/// The Temporal client every host uses: the installation's address and namespace, and its payload codec (docs/07 section
/// 2), whose key is resolved from the secrets provider at startup. Workers take the client's data converter, so the
/// orchestration worker, the agent worker and the API all encrypt and decrypt alike.
/// </summary>
public static class TemporalConnection
{
    public static async Task<ITemporalClient> ConnectAsync(InstallationConfig installation, ProviderSet providers)
    {
        var options = new TemporalClientConnectOptions(installation.TemporalAddress)
        {
            Namespace = installation.TemporalNamespace,
            // A run is one trace (docs/02 section 8, LOG 305), whatever else the client carries.
            Interceptors = [Tracing.Interceptor()],
        };
        if (installation.PayloadCodec is { } codec)
        {
            var secrets = providers.ForRun("secrets", false) as ISecretsProvider
                          ?? throw new InvalidOperationException("temporal.payload_codec.key_ref needs a secrets provider");
            var key = (await secrets.ResolveAsync(codec.KeyRef, CancellationToken.None)) is { Ok: true } found
                ? found.Value!
                : throw new InvalidOperationException($"temporal.payload_codec.key_ref: the secret {codec.KeyRef} does not resolve");
            options.DataConverter = AesGcmPayloadCodec.DataConverterFor(AesGcmPayloadCodec.FromSecret(key));
        }

        return await TemporalClient.ConnectAsync(options);
    }
}
