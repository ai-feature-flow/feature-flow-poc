using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>delivery.report</c> (docs/04 section 12; RW15) over the in-memory world.</summary>
public sealed class DeliveryActionTests
{
    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static List<string> Repos(ActionResult r, string output) => ((IEnumerable<object?>)YamlWriter.ReadPlain(r.Report!.Outputs[output])!).Select(x => (string)x!).ToList();

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A pull request, a passing test.run and ci.checks for the work branch's head: what an engine merge needs.</summary>
    static async Task ReadyAsync(ActionWorld w)
    {
        w.CommitWork("a.txt", "work");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");
        var tests = new TestEvidence(w.Repo.FullName, w.Work!, false, true, [new TestResults(w.Work!, "unit", 3, 0, 0, [])]);
        var reference = (await w.Store.PutAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(tests, Web)), TestContext.Current.CancellationToken)).Unwrap().Id;
        w.Artifacts.Add(new ArtifactEntry("tests", "test_results", "std:artifact_types/test_results", reference, "test.run", true));
        Assert.Equal("passed", Outcome(await w.RunAsync("ci.checks", null, "checks")));
    }

    [Fact]
    public async Task A_repository_the_engine_merged_is_complete()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        await ReadyAsync(w);
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));

        var result = await w.RunAsync("delivery.report", null, "report");

        Assert.Equal("complete", Outcome(result));
        Assert.Equal(["acme/api"], Repos(result, "merged"));
        Assert.Empty(Repos(result, "not_merged"));
    }

    [Fact]
    public async Task A_person_merging_on_the_code_host_counts_as_merged()
    {
        await using var w = new ActionWorld();
        await ReadyAsync(w);
        // The engine may not merge into main (F1): the merge waits for a person, who merges on the code host.
        Assert.Equal("wait", (await w.RunAsync("scm.merge", null, "merge", waits: 1)).Kind);
        w.Forge.MergePullRequest(ActionRunAccess.Pr(w.PullRequests[0]), "octo-person");
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));

        var result = await w.RunAsync("delivery.report", null, "report", produces: [new ArtifactSpec(null, "delivery")]);

        Assert.Equal("complete", Outcome(result));
        var report = JsonSerializer.Deserialize<DeliveryReport>(await w.ReadAsync(result.Report!.Artifacts["delivery"]), Web)!;
        Assert.Equal("octo-person", report.Repositories.Single().MergedBy);
    }

    [Fact]
    public async Task A_run_redirected_past_its_merge_is_partial()
    {
        await using var w = new ActionWorld();
        await ReadyAsync(w);

        var result = await w.RunAsync("delivery.report", null, "report");

        Assert.Equal("partial", Outcome(result));
        Assert.Equal(["acme/api"], Repos(result, "not_merged"));
        Assert.Contains(result.Events, e => e.Contains("no merge record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agents_artifact_named_merge_record_is_not_a_merge()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("a.txt", "work");
        var forged = new MergeEvidence("acme/api", "main", "merge", head, w.Main, w.Main, "feature-flow", false, null);
        var reference = (await w.Store.PutAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(forged, Web)), TestContext.Current.CancellationToken)).Unwrap().Id;
        // The revision is on main, so only the producer check stops it: an agent step made it, and its type is not evidence.
        w.Artifacts.Add(new ArtifactEntry("implement", "merge_record", null, reference));
        w.Artifacts.Add(new ArtifactEntry("implement", "merge_record", "std:artifact_types/merge_record", reference, null, true));

        Assert.Equal("partial", Outcome(await w.RunAsync("delivery.report", null, "report")));
    }

    [Fact]
    public async Task A_merge_the_parent_branch_no_longer_contains_is_not_delivered()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        var before = w.Main;
        await ReadyAsync(w);
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));
        // Someone resets main to before the merge.
        w.Forge.SetReference(w.Repo, "main", before);

        var result = await w.RunAsync("delivery.report", null, "report");

        Assert.Equal("partial", Outcome(result));
        Assert.Contains(result.Events, e => e.Contains("does not contain the recorded merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_named_repository_is_reported_and_one_unmerged_makes_it_partial()
    {
        await using var w = new ActionWorld();
        w.Forge.CreateRepository("acme/web");
        w.EngineMayMerge.Add("acme/api");
        await ReadyAsync(w);
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));

        var result = await w.RunAsync("delivery.report", "{ repositories: [acme/api, acme/web] }", "report");

        Assert.Equal("partial", Outcome(result));
        Assert.Equal(["acme/api"], Repos(result, "merged"));
        Assert.Equal(["acme/web"], Repos(result, "not_merged"));
    }
}
