using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.S3;

/// <summary>Where the store is and how it signs in. Keys are secret references, resolved by the secrets provider.</summary>
public sealed record S3StoreOptions(string Bucket, string Region, string? Endpoint, string? AccessKeyRef, string? SecretKeyRef);

/// <summary>
/// The artifact store on the S3 API (docs/06 section 3.12; LOG 266): AWS S3, or any store that speaks it (MinIO, path
/// style, when <see cref="S3StoreOptions.Endpoint"/> is set). Immutable and content-addressed, like the filesystem
/// store: an artifact is the object <c>sha256/&lt;hex&gt;</c>, written once; a signed URL is a presigned GET. With no
/// key references the SDK's own credential chain applies (an EKS service account's role, for example), so no key is in
/// configuration either way.
/// </summary>
public sealed class S3ArtifactStore(S3StoreOptions options, Func<string, CancellationToken, Task<ProviderResult<string>>> resolveSecret)
    : IArtifactStoreProvider, IDisposable
{
    readonly SemaphoreSlim opening = new(1, 1);
    AmazonS3Client? client;

    public ProviderDescription Describe() => new("artifact_store", "s3", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) =>
        Guarded(async ct =>
        {
            var s3 = await ClientAsync(ct);
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket, MaxKeys = 1 }, ct);
            return default(Unit);
        }, cancellationToken);

    public Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken cancellationToken) =>
        Guarded(async ct =>
        {
            // The key is the hash of the bytes, so they are spooled to a file first and sent only if not already stored.
            var temp = Path.Combine(Path.GetTempPath(), $"ff-s3-put-{Guid.NewGuid():N}");
            try
            {
                string hex;
                await using (var file = File.Create(temp))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }

                    hex = Convert.ToHexStringLower(sha.GetHashAndReset());
                }

                var s3 = await ClientAsync(ct);
                if (!await ExistsAsync(s3, hex, ct))
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = KeyOf(hex), FilePath = temp, ContentType = "application/octet-stream" }, ct);
                return new ArtifactRef("sha256:" + hex);
            }
            finally
            {
                File.Delete(temp);
            }
        }, cancellationToken);

    public async Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken cancellationToken)
    {
        if (Hex(reference) is not { } hex)
            return ProviderResult<Stream>.Failure("not_found", $"no artifact {reference.Id}");
        try
        {
            var s3 = await ClientAsync(cancellationToken);
            var response = await s3.GetObjectAsync(options.Bucket, KeyOf(hex), cancellationToken);
            return ProviderResult<Stream>.Success(response.ResponseStream);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return ProviderResult<Stream>.Failure("not_found", $"no artifact {reference.Id}");
        }
        catch (Exception e) when (Classify(e, cancellationToken) is { } error)
        {
            return new ProviderResult<Stream>(null, error);
        }
    }

    public async Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (Hex(reference) is not { } hex)
            return ProviderResult<Uri>.Failure("not_found", $"no artifact {reference.Id}");
        var signed = await Guarded<Uri?>(async ct =>
        {
            var s3 = await ClientAsync(ct);
            if (!await ExistsAsync(s3, hex, ct))
                return null;
            return new Uri(await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = options.Bucket,
                Key = KeyOf(hex),
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow + ttl,
                Protocol = options.Endpoint is { } e && e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
            }));
        }, cancellationToken);
        return signed is { Ok: true, Value: null } ? ProviderResult<Uri>.Failure("not_found", $"no artifact {reference.Id}") : new ProviderResult<Uri>(signed.Value, signed.Error);
    }

    public void Dispose() => client?.Dispose();

    static string KeyOf(string hex) => "sha256/" + hex;

    static string? Hex(ArtifactRef reference) =>
        reference.Id.StartsWith("sha256:", StringComparison.Ordinal) && reference.Id.Length == 71 && reference.Id[7..].All(Uri.IsHexDigit)
            ? reference.Id[7..].ToLowerInvariant()
            : null;

    async Task<bool> ExistsAsync(AmazonS3Client s3, string hex, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(options.Bucket, KeyOf(hex), ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    async Task<AmazonS3Client> ClientAsync(CancellationToken ct)
    {
        if (client is not null)
            return client;
        await opening.WaitAsync(ct);
        try
        {
            if (client is not null)
                return client;
            var config = new AmazonS3Config { AuthenticationRegion = options.Region };
            if (options.Endpoint is { } endpoint)
            {
                config.ServiceURL = endpoint;
                config.ForcePathStyle = true;
            }
            else
            {
                config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
            }

            client = options.AccessKeyRef is { } accessRef && options.SecretKeyRef is { } secretRef
                ? new AmazonS3Client(new BasicAWSCredentials(await SecretAsync(accessRef, ct), await SecretAsync(secretRef, ct)), config)
                : new AmazonS3Client(config);
            return client;
        }
        finally
        {
            opening.Release();
        }
    }

    async Task<string> SecretAsync(string reference, CancellationToken ct) => (await resolveSecret(reference, ct)).Unwrap();

    async Task<ProviderResult<T>> Guarded<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        try
        {
            return ProviderResult<T>.Success(await work(ct));
        }
        catch (Exception e) when (Classify(e, ct) is { } error)
        {
            return new ProviderResult<T>(default, error);
        }
    }

    /// <summary>
    /// docs/06 section 1.1: a refused key is <c>unauthenticated</c> or <c>forbidden</c>, a missing bucket <c>not_found</c>,
    /// throttling <c>rate_limited</c>, a 5xx <c>server_error</c>, and a store that cannot be reached <c>unreachable</c>.
    /// </summary>
    ProviderError? Classify(Exception e, CancellationToken ct) => e switch
    {
        ProviderException p => p.Error,
        AmazonS3Exception { ErrorCode: "NoSuchBucket" } => new ProviderError("not_found", $"the bucket {options.Bucket} does not exist"),
        AmazonS3Exception { ErrorCode: "SlowDown" } or AmazonS3Exception { StatusCode: HttpStatusCode.TooManyRequests } => new ProviderError("rate_limited", $"S3: {e.Message}"),
        AmazonS3Exception { ErrorCode: "InvalidAccessKeyId" or "SignatureDoesNotMatch" } or AmazonS3Exception { StatusCode: HttpStatusCode.Unauthorized }
            => new ProviderError("unauthenticated", $"S3: {e.Message}"),
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } => new ProviderError("forbidden", $"S3: {e.Message}"),
        AmazonS3Exception { StatusCode: >= HttpStatusCode.InternalServerError } => new ProviderError("server_error", $"S3: {e.Message}"),
        AmazonS3Exception s3 => new ProviderError("rejected", $"S3: {s3.ErrorCode} {s3.Message}"),
        HttpRequestException or AmazonServiceException { InnerException: HttpRequestException } or System.Net.Sockets.SocketException
            => new ProviderError("unreachable", $"S3: {e.Message}"),
        TimeoutException or OperationCanceledException when !ct.IsCancellationRequested => new ProviderError("timeout", $"S3: {e.Message}"),
        _ => null,
    };
}
