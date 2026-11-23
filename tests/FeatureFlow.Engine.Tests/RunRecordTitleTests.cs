using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The record activity titles a run record from its tracker issue (A1): one read of the tracker, kept from the stored record
/// while the issue is the same, and a read that fails leaves the title unset without failing the record.
/// </summary>
public sealed class RunRecordTitleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly InMemoryRecordStore records = new();
    readonly InMemoryTracker tracker = new();

    RunActivities Activities(bool withTracker = true)
    {
        var made = new Dictionary<string, IProvider> { ["record_store"] = records };
        if (withTracker)
            made["tracker"] = tracker;
        var providers = new ProviderSet(made.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, IProvider>)new Dictionary<string, IProvider> { ["one"] = kv.Value }),
            made.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        return new RunActivities(new EngineServices(providers, installation, organisation), null);
    }

    static RunRecord Run(string status, string? issue) =>
        new("run-1", null, "std:templates/ticket_delivery", status, "pat", ["acme/api"], false, DateTimeOffset.UtcNow, null, "ticket", TrackerIssue: issue);

    async Task<RunRecord> RecordAsync(RunRecord run, bool withTracker = true)
    {
        await new ActivityEnvironment().RunAsync(() => Activities(withTracker).RecordAsync(new RecordBatch([run]), null));
        return (await records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = run.RunId }, Ct)).Unwrap().Single();
    }

    [Fact]
    public async Task A_runs_issue_title_is_read_once_and_kept_while_its_issue_is_the_same()
    {
        tracker.Add("ACME-12", new IssueSpec("Add DELETE /widgets/{id}"));
        tracker.Add("ACME-13", new IssueSpec("Page the widgets list"));

        Assert.Equal("Add DELETE /widgets/{id}", (await RecordAsync(Run("running", "ACME-12"))).TrackerTitle);

        // Renamed on the tracker: the record keeps the title it read, without reading again, until the issue changes.
        (await tracker.UpdateAsync(new IssueRef("ACME-12"), new IssueSpec("Renamed"), Ct)).Unwrap();
        tracker.Fail("GetIssue", new ProviderError("unavailable", "a read would fail"));
        Assert.Equal("Add DELETE /widgets/{id}", (await RecordAsync(Run("completed", "ACME-12"))).TrackerTitle);
        // The failure queued is still there for the next read, so the record above read nothing.
        Assert.Null((await RecordAsync(Run("completed", "ACME-13"))).TrackerTitle);
        Assert.Equal("Page the widgets list", (await RecordAsync(Run("completed", "ACME-13"))).TrackerTitle);
    }

    [Fact]
    public async Task A_failed_read_or_no_tracker_records_the_run_untitled()
    {
        tracker.Add("ACME-12", new IssueSpec("Add DELETE /widgets/{id}"));
        tracker.Fail("GetIssue", new ProviderError("unavailable", "the tracker is down"));

        var failed = await RecordAsync(Run("running", "ACME-12"));
        Assert.Equal(("running", "ACME-12", (string?)null), (failed.Status, failed.TrackerIssue, failed.TrackerTitle));
        Assert.Null((await RecordAsync(Run("running", "ACME-12"), withTracker: false)).TrackerTitle);
        Assert.Null((await RecordAsync(Run("running", "ACME-99"))).TrackerTitle);
    }
}
