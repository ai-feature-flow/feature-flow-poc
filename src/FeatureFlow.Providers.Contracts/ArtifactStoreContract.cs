using System.Security.Cryptography;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The artifact store's contract (docs/06 section 3.12): immutable and content-addressed, so a reference is
/// <c>sha256:&lt;hex&gt;</c> of the bytes and storing the same bytes twice gives the same reference; what is stored reads
/// back byte for byte, however large; a reference that names nothing, or is not a reference, is <c>not_found</c>; a signed
/// URL reads the artifact without the store's credentials. An artifact store's test project subclasses it; each test
/// stores bytes of its own, so it can run against a shared bucket.
/// </summary>
public abstract class ArtifactStoreContract : ContractBase
{
    protected abstract IArtifactStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N");

    static string RefOf(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    async Task<ArtifactRef> PutAsync(byte[] bytes) => Ok(await Store.PutAsync(new MemoryStream(bytes), Ct));

    async Task<byte[]> ReadAsync(ArtifactRef reference)
    {
        await using var stream = Ok(await Store.GetAsync(reference, Ct));
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    [Fact]
    public async Task A_reference_is_the_sha256_of_the_bytes_and_the_same_bytes_store_once()
    {
        var bytes = Encoding.UTF8.GetBytes("hello from " + unique);

        var first = await PutAsync(bytes);
        var second = await PutAsync(bytes);
        var other = await PutAsync(Encoding.UTF8.GetBytes("other from " + unique));

        Assert.Equal(RefOf(bytes), first.Id);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(bytes, await ReadAsync(first));
    }

    [Fact]
    public async Task Empty_and_large_artifacts_read_back_byte_for_byte()
    {
        var large = new byte[12 * 1024 * 1024 + 7];
        new Random(unique.GetHashCode(StringComparison.Ordinal)).NextBytes(large);
        Encoding.UTF8.GetBytes(unique).CopyTo(large, 0);

        var empty = await PutAsync([]);
        var big = await PutAsync(large);

        Assert.Equal(RefOf([]), empty.Id);
        Assert.Empty(await ReadAsync(empty));
        Assert.Equal(RefOf(large), big.Id);
        Assert.Equal(large, await ReadAsync(big));
    }

    [Theory]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("../../etc/passwd")]
    [InlineData("sha256:../../etc/passwd")]
    [InlineData("")]
    public async Task A_reference_that_names_nothing_is_not_found(string id)
    {
        Refused(await Store.GetAsync(new ArtifactRef(id), Ct), "not_found");
        Refused(await Store.GetSignedUrlAsync(new ArtifactRef(id), TimeSpan.FromMinutes(5), Ct), "not_found");
    }

    [Fact]
    public async Task A_signed_url_reads_the_artifact()
    {
        var bytes = Encoding.UTF8.GetBytes("signed " + unique);
        var reference = await PutAsync(bytes);

        var url = Ok(await Store.GetSignedUrlAsync(reference, TimeSpan.FromMinutes(5), Ct));

        Assert.Equal(bytes, await FetchAsync(url));
    }

    /// <summary>What a reader with only the URL gets: a file for a local store, an unauthenticated GET otherwise.</summary>
    protected virtual async Task<byte[]> FetchAsync(Uri url)
    {
        if (url.IsFile)
            return await File.ReadAllBytesAsync(url.LocalPath, Ct);
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url, Ct);
    }
}
