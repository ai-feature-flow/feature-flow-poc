using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Tests;

[assembly: AssemblyFixture(typeof(FeatureFlow.Providers.S3.Tests.MinioServer))]

namespace FeatureFlow.Providers.S3.Tests;

/// <summary>
/// One MinIO in a container for the whole assembly, with one bucket; each test stores bytes of its own (LOG 274). MinIO's
/// own images are no longer published, so Chainguard's build is used (LOG 282).
/// </summary>
public sealed class MinioServer : IAsyncLifetime
{
    public const string Image = "cgr.dev/chainguard/minio:latest";
    public const string Bucket = "ff-artifacts";

    public string AccessKey { get; } = "ff" + Guid.NewGuid().ToString("N")[..10];
    public string SecretKey { get; } = Guid.NewGuid().ToString("N");
    public string Endpoint { get; private set; } = "";

    Container? container;

    public async ValueTask InitializeAsync()
    {
        container = await Container.StartAsync(Image, 9000, ["-e", "MINIO_ROOT_USER=" + AccessKey, "-e", "MINIO_ROOT_PASSWORD=" + SecretKey, "--tmpfs", "/data:uid=65532"],
            "server", "/data");
        Endpoint = $"http://{container.Address}";
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                if ((await http.GetAsync($"{Endpoint}/minio/health/ready")).IsSuccessStatusCode)
                    break;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("MinIO did not start");
            await Task.Delay(300);
        }

        using var s3 = Client();
        await s3.PutBucketAsync(Bucket);
    }

    public AmazonS3Client Client() =>
        new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    public S3ArtifactStore Store(string? bucket = null, string? secretKey = null) =>
        new(new S3StoreOptions(bucket ?? Bucket, "us-east-1", Endpoint, "access", "secret"),
            (reference, _) => Task.FromResult(ProviderResult<string>.Success(reference == "access" ? AccessKey : secretKey ?? SecretKey)));

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
public sealed class S3ArtifactStoreContract(MinioServer server) : ArtifactStoreContract, IDisposable
{
    readonly S3ArtifactStore store = server.Store();

    protected override IArtifactStoreProvider Store => store;

    public void Dispose() => store.Dispose();
}

/// <summary>What the contract does not cover: configuration through secret references, failures, and what a signed URL grants.</summary>
[Trait("Category", "Integration")]
public sealed class S3ArtifactStoreTests(MinioServer server) : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-s3-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    IArtifactStoreProvider Configured(string yaml, Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), JsonSerializer.Serialize(secrets));
        var secretsProvider = new FileSecrets(Path.Combine(folder, "secrets.json"));
        var settings = (YMap)YamlText.Parse(yaml, "installation.yaml", new DiagnosticBag())!;
        return (IArtifactStoreProvider)Assert.Single(S3Providers.Factories).Create(new ProviderContext(
            new ProviderInstanceConfig("artifact_store", "s3", "s3", settings), folder, type => type == "secrets" ? secretsProvider : null));
    }

    [Fact]
    public async Task The_keys_come_from_the_secrets_the_installation_names()
    {
        var store = Configured($"{{ bucket: {MinioServer.Bucket}, endpoint: \"{server.Endpoint}\", access_key_ref: secrets/s3_access, secret_key_ref: secrets/s3_secret }}",
            new() { ["s3_access"] = server.AccessKey, ["s3_secret"] = server.SecretKey });
        var bytes = Encoding.UTF8.GetBytes("configured " + Guid.NewGuid());

        Assert.True((await store.CheckAsync(Ct)).Ok);
        var reference = (await store.PutAsync(new MemoryStream(bytes), Ct)).Unwrap();
        await using var read = (await store.GetAsync(reference, Ct)).Unwrap();
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public void One_key_reference_without_the_other_is_a_configuration_error()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Configured("{ bucket: b, access_key_ref: secrets/s3_access }", []));
        Assert.Contains("set together", refused.Message);
    }

    [Fact]
    public async Task A_wrong_key_is_unauthenticated_a_missing_bucket_not_found_and_a_closed_port_unreachable()
    {
        using var wrong = server.Store(secretKey: "not-the-secret");
        using var missing = server.Store(bucket: "no-such-bucket");
        using var closed = new S3ArtifactStore(new S3StoreOptions(MinioServer.Bucket, "us-east-1", "http://127.0.0.1:1", "access", "secret"),
            (_, _) => Task.FromResult(ProviderResult<string>.Success("any")));

        Assert.Equal("unauthenticated", (await wrong.CheckAsync(Ct)).Error?.Reason);
        // A put asks first whether the object exists: S3 answers a HEAD's bad signature with a bare 403, which reads as forbidden.
        Assert.Equal("forbidden", (await wrong.PutAsync(new MemoryStream([1, 2, 3]), Ct)).Error?.Reason);
        Assert.Equal("not_found", (await missing.CheckAsync(Ct)).Error?.Reason);
        Assert.Equal("unreachable", (await closed.CheckAsync(Ct)).Error?.Reason);
    }

    [Fact]
    public async Task Only_the_signature_grants_a_read_and_only_until_it_expires()
    {
        using var store = server.Store();
        var reference = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("expiring " + Guid.NewGuid())), Ct)).Unwrap();
        var url = (await store.GetSignedUrlAsync(reference, TimeSpan.FromSeconds(2), Ct)).Unwrap();
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(url.GetLeftPart(UriPartial.Path), Ct)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_artifact_is_the_object_named_by_its_hash_and_is_written_once()
    {
        using var store = server.Store();
        var bytes = Encoding.UTF8.GetBytes("once " + Guid.NewGuid());
        var reference = (await store.PutAsync(new MemoryStream(bytes), Ct)).Unwrap();
        using var s3 = server.Client();
        var key = "sha256/" + reference.Id["sha256:".Length..];
        var first = (await s3.GetObjectMetadataAsync(MinioServer.Bucket, key, Ct)).LastModified;

        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);
        Assert.Equal(reference, (await store.PutAsync(new MemoryStream(bytes), Ct)).Unwrap());

        Assert.Equal(first, (await s3.GetObjectMetadataAsync(MinioServer.Bucket, key, Ct)).LastModified);
    }
}
