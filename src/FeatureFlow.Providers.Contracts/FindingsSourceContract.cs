using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The findings source provider's contract (docs/06 section 3.7): idempotent requests, polling to <c>ready</c> or
/// <c>unavailable</c>, and a findings report in the library's shape about the target's repository. A provider's test
/// project subclasses it with a target its source reviews and one it cannot.
/// </summary>
public abstract class FindingsSourceContract : ContractBase
{
    protected abstract IFindingsSourceProvider Source { get; }

    /// <summary>Where the source writes its reports.</summary>
    protected abstract IArtifactStoreProvider Store { get; }

    /// <summary>A target the source reviews, raising at least one finding.</summary>
    protected abstract Task<FindingsTarget> ReviewedTargetAsync();

    /// <summary>A target the source cannot review (not enabled there).</summary>
    protected abstract Task<FindingsTarget> UnavailableTargetAsync();

    /// <summary>How long to wait for a request to settle, and between polls.</summary>
    protected virtual TimeSpan Settle => TimeSpan.FromSeconds(10);

    protected virtual TimeSpan PollInterval => TimeSpan.FromMilliseconds(10);

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];

    async Task<CollectStatus> SettleAsync(CollectHandle handle)
    {
        var until = DateTimeOffset.UtcNow + Settle;
        while (true)
        {
            var status = Ok(await Source.GetStatusAsync(handle, Ct));
            if (status.State != CollectStates.Pending || DateTimeOffset.UtcNow > until)
                return status;
            await Task.Delay(PollInterval, Ct);
        }
    }

    [Fact]
    public async Task Requesting_twice_with_one_key_gives_one_handle()
    {
        var target = await ReviewedTargetAsync();

        var first = Ok(await Source.RequestAsync(target, unique, Ct));
        var second = Ok(await Source.RequestAsync(target, unique, Ct));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_reviewed_target_becomes_ready_with_a_findings_report_about_its_repository()
    {
        var target = await ReviewedTargetAsync();
        var handle = Ok(await Source.RequestAsync(target, unique, Ct));

        Assert.Equal(CollectStates.Ready, (await SettleAsync(handle)).State);
        await using var content = Ok(await Store.GetAsync(Ok(await Source.ReadFindingsAsync(handle, Ct)), Ct));
        var report = ArtifactShapes.ReadLibrary<FindingsReport>(await new StreamReader(content, Encoding.UTF8).ReadToEndAsync(Ct), "the findings report");

        Assert.Equal(1, report.Schema);
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f =>
        {
            Assert.Equal(target.Repository.FullName, f.Location.Repository);
            Assert.False(string.IsNullOrWhiteSpace(f.Rule));
            Assert.Contains(f.Severity, FindingSeverities.All);
            Assert.Contains(f.Basis, new[] { FindingBases.Demonstrated, FindingBases.Reasoned });
            Assert.Null(f.LedgerId);
        });
    }

    [Fact]
    public async Task A_target_the_source_cannot_review_is_unavailable_and_has_nothing_to_read()
    {
        var handle = Ok(await Source.RequestAsync(await UnavailableTargetAsync(), unique, Ct));

        var status = await SettleAsync(handle);

        Assert.Equal(CollectStates.Unavailable, status.State);
        Refused(await Source.ReadFindingsAsync(handle, Ct), "validation_failed");
    }

    [Fact]
    public async Task An_unknown_handle_is_not_found()
    {
        Refused(await Source.GetStatusAsync(new CollectHandle(unique + "-404"), Ct), "not_found");
    }
}
