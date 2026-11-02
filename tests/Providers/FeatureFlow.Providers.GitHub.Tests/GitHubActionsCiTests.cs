using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Expectation = FeatureFlow.Providers.GitHub.GitHubActionsCi.Expectation;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>The GitHub Actions provider's mapping of GitHub's responses, and its reading of workflow triggers, without the network.</summary>
public sealed class GitHubActionsCiTests : IDisposable
{
    static readonly RepoRef Repo = new("o/r");
    static readonly string Sha = new('a', 40);
    static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-gha-tests-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;
    readonly FakeGitHub github = new();
    readonly GitHubActionsCi ci;

    public GitHubActionsCiTests()
    {
        store = new FileSystemArtifactStore(folder);
        ci = new GitHubActionsCi(github.Api(), () => store) { DiscoveryWindow = TimeSpan.FromMilliseconds(50), DiscoveryInterval = TimeSpan.FromMilliseconds(10) };
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    static readonly CiJobRef Unit = new("unit.yml", new Dictionary<string, string> { ["tier"] = "unit" });

    const string UnitWorkflow = """
        name: unit
        on:
          workflow_dispatch:
            inputs:
              commit_sha: { required: true, type: string }
              idempotency_key: { required: false, type: string }
          push:
            branches: [ main, "ff/**" ]
        jobs: {}
        """;

    static string File64(string text) =>
        new JsonObject { ["type"] = "file", ["encoding"] = "base64", ["size"] = text.Length, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }.ToJsonString();

    static string Runs(params (long Id, string Sha)[] runs) =>
        new JsonObject { ["workflow_runs"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["head_sha"] = r.Sha, ["html_url"] = $"https://x/{r.Id}" }).ToArray()) }
            .ToJsonString();

    [Fact]
    public async Task A_trigger_tags_the_revision_and_dispatches_on_the_tag_filling_the_declared_inputs()
    {
        var tag = GitHubActionsCi.TagFor("key-1");
        github.On(Post, "~/git/refs", 201, "{}")
            .On(Get, "~/actions/workflows/unit.yml/runs", 200, Runs())
            .On(Get, $"~/contents/.github/workflows/unit.yml?ref={Sha}", 200, File64(UnitWorkflow))
            .On(Post, "~/actions/workflows/unit.yml/dispatches", 200, """{"workflow_run_id":42,"html_url":"https://x/42"}""");

        var run = (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap();

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), run);
        var tagged = JsonNode.Parse(Assert.Single(github.Called(Post, "~/git/refs")).Body)!;
        Assert.Equal(("refs/tags/" + tag, Sha), (tagged["ref"]!.GetValue<string>(), tagged["sha"]!.GetValue<string>()));
        var dispatch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches")).Body)!;
        Assert.Equal(tag, dispatch["ref"]!.GetValue<string>());
        Assert.True(dispatch["return_run_details"]!.GetValue<bool>());
        Assert.Equal(("unit", Sha, "key-1"),
            (dispatch["inputs"]!["tier"]!.GetValue<string>(), dispatch["inputs"]!["commit_sha"]!.GetValue<string>(), dispatch["inputs"]!["idempotency_key"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_retried_trigger_finds_the_tag_and_adopts_its_run_without_dispatching()
    {
        var tag = GitHubActionsCi.TagFor("key-1");
        github.On(Post, "~/git/refs", 422, """{"message":"Reference already exists"}""")
            .On(Get, $"~/git/ref/tags/{tag}", 200, $$$"""{"object":{"sha":"{{{Sha}}}"}}""")
            .On(Get, $"~/actions/workflows/unit.yml/runs?branch={Uri.EscapeDataString(tag)}&event=workflow_dispatch", 200, Runs((41, new string('b', 40)), (42, Sha)));

        var run = (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap();

        Assert.Equal("42", run.Id);
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_trigger_after_its_tag_was_cleaned_up_adopts_the_earlier_run()
    {
        github.On(Post, "~/git/refs", 201, "{}").On(Get, "~/actions/workflows/unit.yml/runs", 200, Runs((42, Sha)));

        Assert.Equal("42", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_dispatch_answered_204_finds_its_run_by_the_tag()
    {
        var listed = 0;
        github.On(Post, "~/git/refs", 201, "{}")
            .On(Get, "~/actions/workflows/unit.yml/runs", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(listed++ < 2 ? Runs() : Runs((43, Sha))) })
            .On(Get, "~/contents/.github/workflows/unit.yml", 200, File64("on: workflow_dispatch\njobs: {}\n"))
            .On(Post, "~/actions/workflows/unit.yml/dispatches", 204);

        Assert.Equal("43", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        var dispatch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches")).Body)!;
        Assert.Null(dispatch["inputs"]!["commit_sha"]);
    }

    [Fact]
    public async Task A_key_reused_at_another_revision_is_refused()
    {
        var tag = GitHubActionsCi.TagFor("key-1");
        github.On(Post, "~/git/refs", 422, """{"message":"Reference already exists"}""")
            .On(Get, $"~/git/ref/tags/{tag}", 200, $$$"""{"object":{"sha":"{{{new string('b', 40)}}}"}}""");

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
    }

    [Theory]
    [InlineData("queued", null, CheckStates.Queued, null)]
    [InlineData("waiting", null, CheckStates.Queued, null)]
    [InlineData("requested", null, CheckStates.Queued, null)]
    [InlineData("pending", null, CheckStates.Queued, null)]
    [InlineData("in_progress", null, CheckStates.InProgress, null)]
    [InlineData("completed", "success", CheckStates.Completed, Conclusions.Success)]
    [InlineData("completed", "failure", CheckStates.Completed, Conclusions.Failure)]
    [InlineData("completed", "startup_failure", CheckStates.Completed, Conclusions.Failure)]
    [InlineData("completed", "cancelled", CheckStates.Completed, Conclusions.Cancelled)]
    [InlineData("completed", "timed_out", CheckStates.Completed, Conclusions.TimedOut)]
    [InlineData("completed", "skipped", CheckStates.Completed, Conclusions.Skipped)]
    [InlineData("completed", "neutral", CheckStates.Completed, Conclusions.Neutral)]
    [InlineData("completed", "action_required", CheckStates.Completed, Conclusions.ActionRequired)]
    public async Task Run_states_and_conclusions_map(string status, string? conclusion, string state, string? expected)
    {
        github.On(Get, "~/actions/runs/42", 200, new JsonObject { ["status"] = status, ["conclusion"] = conclusion }.ToJsonString());
        Assert.Equal(new CiRunStatus(state, expected), (await ci.GetRunStatusAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap());
    }

    [Fact]
    public async Task An_unknown_run_is_not_found()
    {
        github.On(Get, "~/actions/runs/9", 404, """{"message":"Not Found"}""");
        Assert.Equal("not_found", (await ci.GetRunStatusAsync(new CiRunRef(Repo, "9", Sha), Ct)).Error?.Reason);
    }

    static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Results_are_the_jobs_and_the_result_files_from_the_artifacts_and_the_trigger_tag_is_removed()
    {
        var tag = GitHubActionsCi.TagFor("key-1");
        github.On(Get, "~/actions/runs/42/jobs", 200, """{"jobs":[{"name":"unit","conclusion":"success"}]}""")
            .On(Get, "~/actions/runs/42/artifacts", 200, """{"artifacts":[{"id":5,"name":"unit-results","expired":false},{"id":6,"name":"old","expired":true}]}""")
            .On(Get, "~/actions/runs/42", 200, $$"""{"status":"completed","conclusion":"success","head_branch":"{{tag}}"}""")
            .On(Get, "~/actions/artifacts/5/zip", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/5.zip" })
            .Bytes("https://blob.example/5.zip", Zip(("unit.trx", "<TestRun />"), ("junit/results.xml", "<?xml version=\"1.0\"?><testsuites/>"),
                ("analysis.sarif", "{}"), ("notes.txt", "x"), ("other.xml", "<project/>")))
            .On(Delete, $"~/git/refs/tags/{tag}", 204);

        var result = (await ci.ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap();

        Assert.Equal(Conclusions.Success, result.Conclusion);
        Assert.Equal([new CiJobResult("unit", Conclusions.Success)], result.Jobs);
        Assert.Equal([("unit-results/unit.trx", "trx"), ("unit-results/junit/results.xml", "junit"), ("unit-results/analysis.sarif", "sarif")],
            result.Files.Select(f => (f.Name, f.Format)));
        await using var trx = (await store.GetAsync(result.Files[0].Content, Ct)).Unwrap();
        Assert.Equal("<TestRun />", await new StreamReader(trx).ReadToEndAsync(Ct));
        Assert.Single(github.Called(Delete, $"~/git/refs/tags/{tag}"));
        Assert.Empty(github.Called(Get, "~/actions/artifacts/6"));
        // The download carries no GitHub token.
        Assert.DoesNotContain(github.Calls, c => c.Path.StartsWith("https://blob.example", StringComparison.Ordinal) && c.Body.Contains("ghs_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Results_of_a_running_run_are_refused()
    {
        github.On(Get, "~/actions/runs/42", 200, """{"status":"in_progress","conclusion":null}""");
        Assert.Equal("validation_failed", (await ci.ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task Logs_are_the_zip_GitHub_redirects_to_copied_into_the_store()
    {
        github.On(Get, "~/actions/runs/42/logs", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/logs.zip" })
            .Bytes("https://blob.example/logs.zip", [1, 2, 3]);

        var logs = (await ci.ReadLogsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap();

        await using var stream = (await store.GetAsync(logs, Ct)).Unwrap();
        var read = new MemoryStream();
        await stream.CopyToAsync(read, Ct);
        Assert.Equal([1, 2, 3], read.ToArray());
    }

    [Fact]
    public async Task A_logs_answer_that_is_not_a_redirect_is_unexpected()
    {
        github.On(Get, "~/actions/runs/42/logs", 200, "{}");
        Assert.Equal("unexpected_response", (await ci.ReadLogsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task Expected_runs_read_the_workflows_at_the_revision_for_the_branches_it_heads()
    {
        github.On(Get, $"~/contents/.github/workflows?ref={Sha}", 200, """
                [{"name":"unit.yml","type":"file"},{"name":"docs.yaml","type":"file"},{"name":"release.yml","type":"file"},{"name":"README.md","type":"file"}]
                """)
            .On(Get, $"~/commits/{Sha}/branches-where-head", 200, """[{"name":"ff/ticket/T-1"}]""")
            .On(Get, $"~/contents/.github/workflows/unit.yml?ref={Sha}", 200, File64(UnitWorkflow))
            .On(Get, $"~/contents/.github/workflows/docs.yaml?ref={Sha}", 200, File64("on:\n  push:\n    paths: [docs/**]\njobs: {}\n"))
            .On(Get, $"~/contents/.github/workflows/release.yml?ref={Sha}", 200, File64("on:\n  push:\n    tags: [v*]\njobs: {}\n"));

        var expected = (await ci.GetExpectedRunsAsync(Repo, Sha, null, Ct)).Unwrap();

        Assert.Equal(["unit.yml"], expected.Runs);
        Assert.Equal(["docs.yaml"], expected.MayRun);
    }

    [Fact]
    public async Task On_a_pull_request_the_base_and_the_head_branch_decide()
    {
        github.On(Get, $"~/contents/.github/workflows?ref={Sha}", 200, """[{"name":"pr.yml","type":"file"}]""")
            .On(Get, "~/pulls/7", 200, """{"base":{"ref":"main"},"head":{"ref":"feature"}}""")
            .On(Get, $"~/contents/.github/workflows/pr.yml?ref={Sha}", 200, File64("on:\n  pull_request:\n    branches: [main]\njobs: {}\n"));

        Assert.Equal(["pr.yml"], (await ci.GetExpectedRunsAsync(Repo, Sha, new PullRequestRef(Repo, 7), Ct)).Unwrap().Runs);
    }

    [Fact]
    public async Task A_revision_without_workflows_expects_none()
    {
        github.On(Get, $"~/contents/.github/workflows?ref={Sha}", 404, """{"message":"Not Found"}""");
        Assert.False((await ci.GetExpectedRunsAsync(Repo, Sha, null, Ct)).Unwrap().Any);
    }

    [Theory]
    [InlineData("on: push", "main", null, Expectation.Runs)]
    [InlineData("on: [push, pull_request]", "x", null, Expectation.Runs)]
    [InlineData("on: workflow_dispatch", "main", "main", Expectation.None)]
    [InlineData("on: { schedule: [ { cron: '0 0 * * *' } ] }", "main", "main", Expectation.None)]
    [InlineData("on: { push: { branches: [main] } }", "ff/a", null, Expectation.None)]
    [InlineData("on: { push: { branches: ['ff/**'] } }", "ff/ticket/a", null, Expectation.Runs)]
    [InlineData("on: { push: { branches: ['ff/*'] } }", "ff/ticket/a", null, Expectation.None)]
    [InlineData("on: { push: { branches: ['ff/**', '!ff/skip/**'] } }", "ff/skip/a", null, Expectation.None)]
    [InlineData("on: { push: { branches-ignore: ['ff/**'] } }", "ff/a", null, Expectation.None)]
    [InlineData("on: { push: { branches-ignore: ['ff/**'] } }", "main", null, Expectation.Runs)]
    [InlineData("on: { push: { tags: ['v*'] } }", "main", null, Expectation.None)]
    [InlineData("on: { push: { branches: [main], paths: ['src/**'] } }", "main", null, Expectation.MayRun)]
    [InlineData("on: { push: { paths-ignore: ['docs/**'] } }", "main", null, Expectation.MayRun)]
    [InlineData("on: { pull_request: { branches: [main] } }", "x", "main", Expectation.Runs)]
    [InlineData("on: { pull_request: { branches: [release] } }", "x", "main", Expectation.None)]
    [InlineData("on: { pull_request: { types: [labeled] } }", "x", "main", Expectation.MayRun)]
    [InlineData("on: { pull_request: { types: [opened, labeled] } }", "x", "main", Expectation.Runs)]
    [InlineData("on: { pull_request_target: }", "x", "main", Expectation.Runs)]
    [InlineData("on: { pull_request: }", "x", null, Expectation.None)]
    public void Triggers_are_evaluated_as_GitHub_does(string on, string branch, string? pullBase, Expectation expected)
    {
        var workflow = GitHubActionsCi.Workflow.Parse(on + "\njobs: {}\n");
        Assert.NotNull(workflow);
        Assert.Equal(expected, workflow.Expect([branch], pullBase));
    }

    [Theory]
    [InlineData("jobs: {}\n")]
    [InlineData("on: [push\n")]
    public void A_workflow_without_readable_triggers_does_not_parse(string text)
    {
        Assert.Null(GitHubActionsCi.Workflow.Parse(text));
    }
}
