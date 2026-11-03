using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The findings source contract against Copilot review on the fixture repository, through the GitHub App. Each test opens
/// a pull request of a small change under its own prefix and asks Copilot to review it. Where Copilot code review is not
/// enabled for the fixture, the source must say <c>unavailable</c>: the tests that need a review are skipped with that
/// result, and the unavailable test runs on the fixture's pull request itself. <see cref="LiveFixture.CleanUpAsync"/>
/// closes the pull request and deletes the branches.
/// </summary>
[Trait("Category", "Live")]
public sealed class CopilotReviewSourceLiveContract : FindingsSourceContract, IAsyncLifetime
{
    readonly LiveFixture fixture = new();
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-copilot-live-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;
    FindingsTarget? target;
    CollectStatus? copilot;

    public CopilotReviewSourceLiveContract()
    {
        store = new FileSystemArtifactStore(folder);
        Source = new CopilotReviewSource(fixture.Api, () => store);
    }

    protected override IFindingsSourceProvider Source { get; }

    protected override IArtifactStoreProvider Store => store;

    protected override TimeSpan Settle => TimeSpan.FromMinutes(5);

    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(10);

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await fixture.CreateBaseAsync();
        var head = await fixture.CommitAsync(root, new Dictionary<string, string?>
        {
            ["src/Orders.cs"] = "public class Orders\n{\n    public decimal Total(Order order) => order.Lines.Sum(l => l.Price) / order.Lines.Count;\n}\n",
        }, "Add an order total");
        await fixture.SetBranchAsync(fixture.Branch("work"), head);
        var pr = (await new GitHubCodeHost(fixture.Api).OpenPullRequestAsync(LiveFixture.Repo,
            new PullRequestSpec("Copilot review contract", "A change for the findings source contract.", fixture.Branch("work"), fixture.Branch("main")),
            $"{fixture.Prefix}/copilot", ct)).Unwrap();
        target = new FindingsTarget(LiveFixture.Repo, head, pr);

        // Whether Copilot reviews here: asked once, then polled until it reviews or is plainly not going to.
        var handle = (await Source.RequestAsync(target, $"{fixture.Prefix}/probe", ct)).Unwrap();
        var until = DateTimeOffset.UtcNow + Settle;
        do
        {
            copilot = (await Source.GetStatusAsync(handle, ct)).Unwrap();
            if (copilot.State != CollectStates.Pending)
                break;
            await Task.Delay(PollInterval, ct);
        }
        while (DateTimeOffset.UtcNow < until);

        LiveFixture.Log($"copilot review on {LiveFixture.Repo.FullName}#{pr.Number}: {copilot.State} {copilot.Reason}");
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.CleanUpAsync();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override Task<FindingsTarget> ReviewedTargetAsync()
    {
        if (copilot!.State != CollectStates.Ready)
            Assert.Skip($"Copilot code review does not review the fixture ({copilot.State}: {copilot.Reason}); the unavailable test covers it");
        return Task.FromResult(target!);
    }

    /// <summary>The fixture's pull request where Copilot is not enabled; otherwise the same change with no pull request.</summary>
    protected override Task<FindingsTarget> UnavailableTargetAsync() =>
        Task.FromResult(copilot!.State == CollectStates.Ready ? target! with { PullRequest = null } : target!);

    [Fact]
    public void Copilot_review_on_the_fixture_settles_as_ready_or_unavailable()
    {
        Assert.NotEqual(CollectStates.Pending, copilot!.State);
        TestContext.Current.SendDiagnosticMessage($"Copilot review on the fixture: {copilot.State} ({copilot.Reason})");
    }
}
