using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>POST /runs</c> holds a start to check 18 before the run exists (LOG 495): a required parameter missing, or a literal
/// of the wrong type, is refused with 400 naming each, and nothing is started. It also settles the run's tracker issue (LOG 501).
/// </summary>
public sealed class StartRunTests : IAsyncDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-start-" + Guid.NewGuid().ToString("N"));
    readonly ApiWorld world;

    public StartRunTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\nentrypoints:\n  - { template: needs }\n  - { template: tracked }\n");
        File.WriteAllText(Path.Combine(root, "templates", "tracked.yaml"), """
            api_version: feature-flow/v1
            kind: template
            metadata: { name: tracked, level: ticket, tracker_parameter: ticket_id }
            parameters:
              ticket_id: { type: string, required: true }
            steps:
              - id: comment
                action: tracker.update
                with: { op: comment, issue: "${params.ticket_id}", body: started }
                routes:
                  done: { end: completed }
            """);
        File.WriteAllText(Path.Combine(root, "templates", "needs.yaml"), """
            api_version: feature-flow/v1
            kind: template
            metadata: { name: needs, level: ticket }
            parameters:
              ticket_id: { type: string, required: true }
              attempts: { type: number, required: true }
              branch: { type: string, default: main }
            steps:
              - id: comment
                action: tracker.update
                with: { op: comment, issue: "${params.ticket_id}", body: started }
                routes:
                  done: { end: completed }
            """);
        world = new ApiWorld(new OrganisationConfig("t", [new LibrarySourceConfig("probe", "directory", root)], new PolicyConfig([], null, null, null, null), ""));
    }

    public async ValueTask DisposeAsync()
    {
        await world.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    async Task<(HttpStatusCode Status, string Body)> StartAsync(string parameters, string template = "needs", string trackerIssue = "")
    {
        var issue = trackerIssue.Length == 0 ? "" : $$""", "tracker_issue": "{{trackerIssue}}" """;
        var (status, body) = await ApiWorld.PostAsync(world.As("eve", "steerer"), "runs", $$"""{ "template": "probe:templates/{{template}}", "parameters": {{parameters}}{{issue}} }""");
        return (status, status == HttpStatusCode.BadRequest ? JsonSerializer.Deserialize<ErrorResponse>(body, ApiJson.Options)!.Error : body);
    }

    [Fact]
    public async Task A_template_that_names_its_tracker_parameter_takes_the_runs_tracker_issue_from_it()
    {
        // LOG 501: one source for the ticket. The same key given twice is accepted; another is refused naming both.
        var (derived, derivedBody) = await StartAsync("""{ "ticket_id": "\"ACME-1\"" }""", "tracked");
        var (same, sameBody) = await StartAsync("""{ "ticket_id": "\"ACME-2\"" }""", "tracked", "ACME-2");
        var (other, otherBody) = await StartAsync("""{ "ticket_id": "\"ACME-3\"" }""", "tracked", "ACME-4");

        Assert.True((derived, same) == (HttpStatusCode.Created, HttpStatusCode.Created), derivedBody + sameBody);
        Assert.Equal(["ACME-1", "ACME-2"], world.Temporal.Starts.Select(s => s.TrackerIssue));
        Assert.Equal(HttpStatusCode.BadRequest, other);
        Assert.Equal("tracker_issue 'ACME-4' is not parameter ticket_id 'ACME-3', which is this template's tracker issue; give only ticket_id", otherBody);
    }

    [Fact]
    public async Task A_template_that_names_no_tracker_parameter_takes_the_requested_tracker_issue()
    {
        var (status, body) = await StartAsync("""{ "ticket_id": "\"ACME-1\"", "attempts": "3" }""", trackerIssue: "ACME-9");

        Assert.True(status == HttpStatusCode.Created, body);
        Assert.Equal("ACME-9", Assert.Single(world.Temporal.Starts).TrackerIssue);
    }

    [Fact]
    public async Task A_start_missing_required_parameters_is_refused_naming_each()
    {
        var (status, body) = await StartAsync("{}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("required parameter 'ticket_id' is not bound", body, StringComparison.Ordinal);
        Assert.Contains("required parameter 'attempts' is not bound", body, StringComparison.Ordinal);
        Assert.DoesNotContain("branch", body, StringComparison.Ordinal);
        Assert.Empty(world.Temporal.Starts);
    }

    [Fact]
    public async Task A_literal_of_the_wrong_type_is_refused()
    {
        var (status, body) = await StartAsync("""{ "ticket_id": "\"ACME-1\"", "attempts": "\"three\"" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("parameter 'attempts' (number)", body, StringComparison.Ordinal);
        Assert.Empty(world.Temporal.Starts);
    }

    [Fact]
    public async Task A_start_with_every_required_parameter_is_accepted()
    {
        var (status, body) = await StartAsync("""{ "ticket_id": "\"ACME-1\"", "attempts": "3" }""");

        Assert.True(status == HttpStatusCode.Created, body);
        var start = Assert.Single(world.Temporal.Starts);
        Assert.Equal(("probe:templates/needs", "\"ACME-1\"", "3"), (start.Template, start.Parameters["ticket_id"], start.Parameters["attempts"]));
    }
}
