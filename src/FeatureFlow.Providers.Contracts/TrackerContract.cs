using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The tracker provider's contract (docs/06 section 3.6): idempotent creates and comments, partial updates, transitions by
/// library status name, idempotent links, and search. A provider's test project subclasses it; each test makes its own
/// issues with unique keys and labels, so it can run against a shared real tracker.
/// </summary>
public abstract class TrackerContract : ContractBase
{
    protected abstract ITrackerProvider Tracker { get; }

    /// <summary>Two library statuses the provider's configuration maps, such as <c>in_progress</c> and <c>done</c>.</summary>
    protected abstract (string First, string Second) Statuses { get; }

    /// <summary>The bodies of an issue's comments, read from the tracker as a person would see them.</summary>
    protected abstract Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue);

    /// <summary>Unique within this test instance, so tests never see each other's issues.</summary>
    protected readonly string Unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];

    Task<ProviderResult<IssueRef>> CreateAsync(string key, string title = "A ticket", IssueRef? parent = null) =>
        Tracker.CreateAsync(new IssueSpec(title, "The body.", [Unique], parent), $"{Unique}-{key}", Ct);

    [Fact]
    public async Task Creating_twice_with_one_key_creates_one_issue()
    {
        var first = Ok(await CreateAsync("twice"));
        var second = Ok(await CreateAsync("twice", "Retried"));

        Assert.Equal(first, second);
        Assert.Single(Ok(await Tracker.SearchAsync(new IssueQuery(Label: Unique), Ct)));
    }

    [Fact]
    public async Task A_created_issue_reads_back_with_its_title_body_labels_and_parent()
    {
        var parent = Ok(await CreateAsync("parent", "The epic"));
        var child = Ok(await CreateAsync("child", "The ticket", parent));

        var issue = Ok(await Tracker.GetIssueAsync(child, Ct));

        Assert.Equal((child, "The ticket", "The body."), (issue.Ref, issue.Title, issue.Body));
        Assert.Contains(Unique, issue.Labels);
        Assert.Equal(parent, issue.Parent);
    }

    [Fact]
    public async Task An_unknown_issue_is_not_found()
    {
        Refused(await Tracker.GetIssueAsync(new IssueRef(Unique + "-404"), Ct), "not_found");
    }

    [Fact]
    public async Task An_update_changes_only_what_it_gives()
    {
        var issue = Ok(await CreateAsync("update"));

        Ok(await Tracker.UpdateAsync(issue, new IssueSpec(Title: "Renamed"), Ct));

        var read = Ok(await Tracker.GetIssueAsync(issue, Ct));
        Assert.Equal(("Renamed", "The body."), (read.Title, read.Body));
        Assert.Contains(Unique, read.Labels);
    }

    [Fact]
    public async Task A_transition_moves_to_a_library_status_and_repeating_it_succeeds()
    {
        var issue = Ok(await CreateAsync("transition"));
        var (first, second) = Statuses;

        Ok(await Tracker.TransitionAsync(issue, first, Ct));
        Assert.Equal(first, Ok(await Tracker.GetIssueAsync(issue, Ct)).Status);

        Ok(await Tracker.TransitionAsync(issue, second, Ct));
        Ok(await Tracker.TransitionAsync(issue, second, Ct));
        Assert.Equal(second, Ok(await Tracker.GetIssueAsync(issue, Ct)).Status);
    }

    [Fact]
    public async Task A_status_the_configuration_does_not_map_is_refused()
    {
        var issue = Ok(await CreateAsync("unmapped"));

        Refused(await Tracker.TransitionAsync(issue, "no_such_library_status", Ct), "validation_failed");
    }

    [Fact]
    public async Task Linking_twice_succeeds()
    {
        var from = Ok(await CreateAsync("link-from"));
        var to = Ok(await CreateAsync("link-to"));

        Ok(await Tracker.LinkAsync(from, to, "relates", Ct));
        Ok(await Tracker.LinkAsync(from, to, "relates", Ct));
    }

    [Fact]
    public async Task Commenting_twice_with_one_key_adds_one_comment()
    {
        var issue = Ok(await CreateAsync("comment"));

        var first = Ok(await Tracker.CommentAsync(issue, "Started.", $"{Unique}-c1", Ct));
        var second = Ok(await Tracker.CommentAsync(issue, "Started.", $"{Unique}-c1", Ct));
        Ok(await Tracker.CommentAsync(issue, "Done.", $"{Unique}-c2", Ct));

        Assert.Equal(first, second);
        var comments = await CommentsAsync(issue);
        Assert.Equal(2, comments.Count);
        Assert.Single(comments, c => c.Contains("Started.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_finds_by_label_parent_and_marker()
    {
        var parent = Ok(await CreateAsync("search-parent"));
        var child = Ok(await CreateAsync("search-child", "Child", parent));

        Assert.Equal(2, Ok(await Tracker.SearchAsync(new IssueQuery(Label: Unique), Ct)).Count);
        Assert.Equal([child], Ok(await Tracker.SearchAsync(new IssueQuery(Parent: parent), Ct)).Select(i => i.Ref));
        Assert.Equal([parent], Ok(await Tracker.SearchAsync(new IssueQuery(Marker: $"{Unique}-search-parent"), Ct)).Select(i => i.Ref));
        Assert.Empty(Ok(await Tracker.SearchAsync(new IssueQuery(Label: Unique + "-none"), Ct)));
    }
}
