using System.Text.Json.Nodes;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Jira.Tests;

/// <summary>The tracker contract for the Jira provider, against Jira in memory (<see cref="FakeJira"/>), whose search index lags.</summary>
public sealed class JiraTrackerContract : TrackerContract
{
    // The newest issue is left out of every search, as Jira's index lags a create.
    readonly FakeJira jira = new() { Unlisted = 1 };

    public JiraTrackerContract() => Tracker = new JiraTracker(jira.Api(), "ACME");

    protected override ITrackerProvider Tracker { get; }

    protected override (string First, string Second) Statuses => ("in_progress", "done");

    protected override Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue) =>
        Task.FromResult<IReadOnlyList<string>>(jira.Issue(issue.Key).Comments.Select(c => JiraTracker.Text(c.Body)).ToList());
}

/// <summary>The Jira tracker's mapping of Jira's answers (docs/06 section 1.1), of statuses to the workflow, and of parents, links and markers.</summary>
public sealed class JiraTrackerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<(FakeJira Jira, JiraTracker Tracker)> WithIssueAsync(IReadOnlyDictionary<string, string>? map = null)
    {
        var jira = new FakeJira();
        var tracker = new JiraTracker(jira.Api(), "ACME", map);
        Assert.True((await tracker.CreateAsync(new IssueSpec("A ticket", "Line one.\n\nLine three."), "k1", Ct)).Ok);
        return (jira, tracker);
    }

    [Fact]
    public async Task A_retried_create_adopts_its_issue_when_a_later_one_was_indexed_first()
    {
        var jira = new FakeJira();
        var tracker = new JiraTracker(jira.Api(), "ACME");
        var first = (await tracker.CreateAsync(new IssueSpec("Mine"), "mine", Ct)).Value!;
        Assert.True((await tracker.CreateAsync(new IssueSpec("Someone else's"), "theirs", Ct)).Ok);
        jira.Unindexed.Add(first.Key);

        var retried = await tracker.CreateAsync(new IssueSpec("Mine"), "mine", Ct);

        Assert.Equal(first, retried.Value);
        Assert.Equal(2, jira.Issues.Count);
    }

    [Fact]
    public async Task A_body_round_trips_as_paragraphs_and_the_key_label_is_not_one_of_its_labels()
    {
        var (jira, tracker) = await WithIssueAsync();

        var issue = (await tracker.GetIssueAsync(new IssueRef("ACME-1"), Ct)).Value!;

        Assert.Equal("Line one.\n\nLine three.", issue.Body);
        Assert.Equal(3, jira.Issue("ACME-1").Description!["content"]!.AsArray().Count);
        Assert.Equal([JiraTracker.MarkerLabel("k1")], jira.Issue("ACME-1").Labels);
        Assert.Empty(issue.Labels);
        Assert.Equal($"{FakeJira.Base}/browse/ACME-1", issue.Url);
    }

    [Fact]
    public async Task Updating_labels_keeps_the_key_label_so_a_retried_create_still_adopts()
    {
        var (jira, tracker) = await WithIssueAsync();

        Assert.True((await tracker.UpdateAsync(new IssueRef("ACME-1"), new IssueSpec(Labels: ["team-a"]), Ct)).Ok);

        Assert.Equal(["team-a", JiraTracker.MarkerLabel("k1")], jira.Issue("ACME-1").Labels);
        Assert.Equal("ACME-1", (await tracker.CreateAsync(new IssueSpec("Retried"), "k1", Ct)).Value!.Key);
        Assert.Single(jira.Issues);
    }

    [Fact]
    public async Task A_status_reads_as_the_library_status_its_jira_status_maps_to_and_otherwise_as_jiras_own()
    {
        var (jira, tracker) = await WithIssueAsync(new Dictionary<string, string> { ["in_progress"] = "in progress" });

        Assert.Equal("To Do", (await tracker.GetIssueAsync(new IssueRef("ACME-1"), Ct)).Value!.Status);
        jira.Issue("ACME-1").Status = "In Progress";
        Assert.Equal("in_progress", (await tracker.GetIssueAsync(new IssueRef("ACME-1"), Ct)).Value!.Status);
    }

    [Fact]
    public async Task A_status_the_workflow_offers_no_transition_to_is_refused_and_nothing_moves()
    {
        var (jira, tracker) = await WithIssueAsync();
        jira.Answer(HttpMethod.Get, "/rest/api/3/issue/ACME-1/transitions", 200, """{ "transitions": [ { "id": "11", "name": "Start", "to": { "name": "In Progress" } } ] }""");

        var refused = await tracker.TransitionAsync(new IssueRef("ACME-1"), "done", Ct);

        Assert.Equal("validation_failed", refused.Error!.Reason);
        Assert.Contains("no transition from 'To Do' to 'Done'", refused.Error.Message);
        Assert.Equal("To Do", jira.Issue("ACME-1").Status);
        Assert.DoesNotContain(jira.Calls, c => c.Method == HttpMethod.Post && c.Path.EndsWith("/transitions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transition_is_chosen_by_the_status_it_leads_to()
    {
        var (jira, tracker) = await WithIssueAsync();

        Assert.True((await tracker.TransitionAsync(new IssueRef("ACME-1"), "code_review", Ct)).Ok);

        Assert.Equal("In Review", jira.Issue("ACME-1").Status);
    }

    [Fact]
    public async Task A_child_of_an_epic_takes_the_configured_type_and_a_child_of_another_issue_is_a_subtask()
    {
        var jira = new FakeJira();
        var tracker = new JiraTracker(jira.Api(), "ACME", issueType: "Story");
        var task = (await tracker.CreateAsync(new IssueSpec("A task"), "task", Ct)).Value!;
        jira.Issues.Add(new FakeJira.FakeIssue { Key = "ACME-9", Project = "ACME", Type = "Epic", Summary = "An epic" });

        var underEpic = (await tracker.CreateAsync(new IssueSpec("Story", Parent: new IssueRef("ACME-9")), "s", Ct)).Value!;
        var underTask = (await tracker.CreateAsync(new IssueSpec("Step", Parent: task), "t", Ct)).Value!;

        Assert.Equal(("Story", "ACME-9"), (jira.Issue(underEpic.Key).Type, jira.Issue(underEpic.Key).Parent));
        Assert.Equal(("Subtask", task.Key), (jira.Issue(underTask.Key).Type, jira.Issue(underTask.Key).Parent));
        Assert.Equal("validation_failed", (await tracker.CreateAsync(new IssueSpec("Deeper", Parent: underTask), "d", Ct)).Error!.Reason);
        Assert.Equal("not_found", (await tracker.CreateAsync(new IssueSpec("Orphan", Parent: new IssueRef("ACME-404")), "o", Ct)).Error!.Reason);
    }

    [Fact]
    public async Task A_link_reads_as_the_first_issue_blocks_the_second_and_an_unknown_link_type_is_refused()
    {
        var (jira, tracker) = await WithIssueAsync();
        Assert.True((await tracker.CreateAsync(new IssueSpec("Another"), "k2", Ct)).Ok);

        Assert.True((await tracker.LinkAsync(new IssueRef("ACME-1"), new IssueRef("ACME-2"), "blocks", Ct)).Ok);
        Assert.True((await tracker.LinkAsync(new IssueRef("ACME-1"), new IssueRef("ACME-2"), "Blocks", Ct)).Ok);

        Assert.Equal([("Blocks", "ACME-1", "ACME-2")], jira.Issue("ACME-1").Links);
        Assert.Equal("validation_failed", (await tracker.LinkAsync(new IssueRef("ACME-1"), new IssueRef("ACME-2"), "causes chaos", Ct)).Error!.Reason);
        Assert.Equal("not_found", (await tracker.LinkAsync(new IssueRef("ACME-1"), new IssueRef("ACME-404"), "relates", Ct)).Error!.Reason);
    }

    [Fact]
    public async Task A_comment_carries_its_key_in_a_hidden_property_not_in_its_text()
    {
        var (jira, tracker) = await WithIssueAsync();

        var comment = (await tracker.CommentAsync(new IssueRef("ACME-1"), "Started.", "c1", Ct)).Value!;

        var stored = jira.Issue("ACME-1").Comments.Single();
        Assert.Equal((comment.Id, "Started.", "c1"), (stored.Id, JiraTracker.Text(stored.Body), stored.Key));
    }

    [Theory]
    [InlineData(404, "", "not_found")]
    [InlineData(400, """{ "errors": { "summary": "required" } }""", "validation_failed")]
    [InlineData(409, "", "conflict")]
    [InlineData(500, "", "server_error")]
    [InlineData(503, "", "server_error")]
    [InlineData(403, """{ "errorMessages": ["You do not have permission"] }""", "forbidden")]
    public async Task Jiras_error_statuses_are_classified(int status, string body, string reason)
    {
        var jira = new FakeJira().Answer(HttpMethod.Get, "/rest/api/3/issue/ACME-7", status, body);

        var read = await new JiraTracker(jira.Api(), "ACME").GetIssueAsync(new IssueRef("ACME-7"), Ct);

        Assert.Equal(reason, read.Error!.Reason);
    }

    [Fact]
    public async Task Rate_limiting_carries_retry_after()
    {
        var jira = new FakeJira();
        var handler = new RetryAfter(jira);
        var api = new JiraApi(_ => Task.FromResult(ProviderResult<string>.Success(jira.Credential)), FakeJira.Base, handler);

        var read = await new JiraTracker(api, "ACME").GetIssueAsync(new IssueRef("ACME-7"), Ct);

        Assert.Equal(("rate_limited", TimeSpan.FromSeconds(7)), (read.Error!.Reason, read.Error.RetryAfter));
    }

    sealed class RetryAfter(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_refused_credential_is_resolved_again_once_then_unauthenticated()
    {
        var jira = new FakeJira();
        var resolved = 0;
        var api = new JiraApi(_ =>
        {
            resolved++;
            return Task.FromResult(ProviderResult<string>.Success("bot@example.com:revoked"));
        }, FakeJira.Base, jira) { FreshCredentialDelay = TimeSpan.FromMilliseconds(10) };

        var read = await new JiraTracker(api, "ACME").GetIssueAsync(new IssueRef("ACME-1"), Ct);

        Assert.Equal(("unauthenticated", 2, 2), (read.Error!.Reason, resolved, jira.Calls.Count));
    }

    [Fact]
    public async Task A_secret_that_is_not_email_and_token_is_unauthenticated_and_never_quoted()
    {
        var api = new JiraApi(_ => Task.FromResult(ProviderResult<string>.Success("tok-secret-value")), FakeJira.Base, new FakeJira());

        var read = await new JiraTracker(api, "ACME").CheckAsync(Ct);

        Assert.Equal("unauthenticated", read.Error!.Reason);
        Assert.DoesNotContain("tok-secret-value", read.Error.Message);
    }

    [Fact]
    public async Task An_unreachable_site_is_infrastructure()
    {
        var api = new JiraApi(_ => Task.FromResult(ProviderResult<string>.Success("a@b:c")), FakeJira.Base, new Unreachable());

        var read = await new JiraTracker(api, "ACME").GetIssueAsync(new IssueRef("ACME-1"), Ct);

        Assert.Equal((ProviderErrorClass.Infrastructure, "unreachable"), (read.Error!.Class, read.Error.Reason));
    }

    sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }

    [Fact]
    public async Task A_create_answered_with_an_undocumented_success_is_unexpected_response()
    {
        var jira = new FakeJira().Answer(HttpMethod.Post, "/rest/api/3/issue", 200, """{ "key": "ACME-1" }""");

        var created = await new JiraTracker(jira.Api(), "ACME").CreateAsync(new IssueSpec("T"), "k", Ct);

        Assert.Equal("unexpected_response", created.Error!.Reason);
    }

    [Fact]
    public async Task Custom_fields_and_a_move_to_another_project_are_refused()
    {
        var (_, tracker) = await WithIssueAsync();

        Assert.Equal("validation_failed", (await tracker.CreateAsync(new IssueSpec("T", Fields: new Dictionary<string, string> { ["points"] = "3" }), "f", Ct)).Error!.Reason);
        Assert.Equal("validation_failed", (await tracker.UpdateAsync(new IssueRef("ACME-1"), new IssueSpec(Project: "OTHER"), Ct)).Error!.Reason);
    }

    [Fact]
    public void The_factory_reads_its_settings_and_refuses_an_insecure_site_and_a_malformed_status_map()
    {
        var factory = Assert.Single(JiraProviders.Factories);
        IProvider Create(string yaml) =>
            factory.Create(new ProviderContext(new ProviderInstanceConfig("tracker", "jira", "jira", (YMap)YamlWriter.Read(yaml, "settings")), ".", _ => null));

        Assert.Equal("jira", Create("base_url: \"https://acme.example\"\nsecret_ref: secrets/jira\nproject: ACME\nstatus_map: { done: Closed }").Describe().Name);
        Assert.Contains("must be an https URL", Assert.Throws<InvalidOperationException>(() => Create("base_url: \"http://acme.example\"\nsecret_ref: s")).Message);
        Assert.Contains("needs 'secret_ref'", Assert.Throws<InvalidOperationException>(() => Create("base_url: \"https://acme.example\"")).Message);
        Assert.Contains("status_map maps each", Assert.Throws<InvalidOperationException>(() => Create("base_url: \"https://a.example\"\nsecret_ref: s\nstatus_map: [x]")).Message);
    }

    [Fact]
    public void A_document_reads_lists_headings_and_hard_breaks_as_lines()
    {
        var document = JsonNode.Parse("""
            { "type": "doc", "version": 1, "content": [
              { "type": "heading", "content": [ { "type": "text", "text": "Title" } ] },
              { "type": "bulletList", "content": [ { "type": "listItem", "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "one" } ] } ] } ] },
              { "type": "paragraph", "content": [ { "type": "text", "text": "a" }, { "type": "hardBreak" }, { "type": "text", "text": "b" } ] } ] }
            """);

        Assert.Equal("Title\none\na\nb", JiraTracker.Text(document));
    }
}
