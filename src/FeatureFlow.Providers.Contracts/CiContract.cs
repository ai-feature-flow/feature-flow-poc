using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The CI provider's contract (docs/06 section 3.5): idempotent triggers, polled runs, results and logs read into
/// the artifact store, and the runs expected for a head. A provider's test project subclasses it.
/// </summary>
public abstract class CiContract : ContractBase
{
    protected abstract ICiProvider Ci { get; }

    protected abstract RepoRef Repo { get; }

    /// <summary>A job that passes and produces at least one result file.</summary>
    protected abstract CiJobRef PassingJob { get; }

    /// <summary>A revision of <see cref="Repo"/> for which CI is configured to run (<paramref name="withCi"/>), or not configured at all.</summary>
    protected abstract Task<string> HeadAsync(bool withCi);

    /// <summary>How long a run may take to complete.</summary>
    protected virtual TimeSpan RunTimeout => TimeSpan.FromSeconds(30);

    protected virtual TimeSpan PollInterval => TimeSpan.Zero;

    async Task<CiRunStatus> CompleteAsync(CiRunRef run)
    {
        var deadline = DateTimeOffset.UtcNow + RunTimeout;
        while (true)
        {
            var status = Ok(await Ci.GetRunStatusAsync(run, Ct));
            if (status.State == CheckStates.Completed)
                return status;
            Assert.True(DateTimeOffset.UtcNow < deadline, $"run {run.Id} did not complete in {RunTimeout}");
            await Task.Delay(PollInterval, Ct);
        }
    }

    [Fact]
    public async Task Triggering_twice_with_one_key_starts_one_run()
    {
        var head = await HeadAsync(withCi: true);

        var first = Ok(await Ci.TriggerAsync(Repo, PassingJob, head, "key-trigger", Ct));
        var second = Ok(await Ci.TriggerAsync(Repo, PassingJob, head, "key-trigger", Ct));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(head, first.Revision);
    }

    [Fact]
    public async Task A_run_is_polled_to_completion_and_its_results_read()
    {
        var head = await HeadAsync(withCi: true);
        var run = Ok(await Ci.TriggerAsync(Repo, PassingJob, head, "key-results", Ct));

        var status = await CompleteAsync(run);
        var result = Ok(await Ci.ReadResultsAsync(run, Ct));

        Assert.Equal(Conclusions.Success, status.Conclusion);
        Assert.Equal(Conclusions.Success, result.Conclusion);
        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, f => Assert.Contains(f.Format, new[] { ResultFormats.Trx, ResultFormats.Junit, ResultFormats.Sarif }));
        Assert.NotEmpty(Ok(await Ci.ReadLogsAsync(run, Ct)).Id);
    }

    [Fact]
    public async Task An_unknown_run_is_not_found()
    {
        var head = await HeadAsync(withCi: true);
        Refused(await Ci.GetRunStatusAsync(new CiRunRef(Repo, "999999999", head), Ct), "not_found");
    }

    [Fact]
    public async Task A_head_without_CI_expects_no_runs()
    {
        var head = await HeadAsync(withCi: false);
        Assert.False(Ok(await Ci.GetExpectedRunsAsync(Repo, head, null, Ct)).Any);
    }

    [Fact]
    public async Task A_head_with_CI_expects_runs()
    {
        var head = await HeadAsync(withCi: true);
        Assert.True(Ok(await Ci.GetExpectedRunsAsync(Repo, head, null, Ct)).Any);
    }
}
