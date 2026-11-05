using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.S3;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class S3Providers
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("artifact_store", "s3", ["bucket", "region", "endpoint", "access_key_ref", "secret_key_ref"], Create),
    ];

    /// <summary>
    /// The artifact store on the S3 API (LOG 266). <c>bucket</c> is required; <c>region</c> defaults to <c>us-east-1</c>;
    /// <c>endpoint</c> names a store other than AWS, such as MinIO, addressed path-style. <c>access_key_ref</c> and
    /// <c>secret_key_ref</c> are secret references, set together; without them the AWS SDK's own credential chain applies.
    /// </summary>
    static S3ArtifactStore Create(ProviderContext c)
    {
        var where = $"providers.artifact_store.instances.{c.Instance.Name}";
        var (accessRef, secretRef) = (c.Setting("access_key_ref"), c.Setting("secret_key_ref"));
        if (accessRef is null != secretRef is null)
            throw new InvalidOperationException($"{where}: access_key_ref and secret_key_ref are set together or not at all");
        return new S3ArtifactStore(new S3StoreOptions(c.RequiredSetting("bucket"), c.Setting("region") ?? "us-east-1", c.Setting("endpoint"), accessRef, secretRef),
            (reference, ct) => (c.Provider("secrets") as ISecretsProvider ?? throw new InvalidOperationException($"{where} needs a secrets provider"))
                .ResolveAsync(reference, ct));
    }
}
