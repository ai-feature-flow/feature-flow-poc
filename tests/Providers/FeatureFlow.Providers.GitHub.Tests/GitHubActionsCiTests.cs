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
    static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Patch = HttpMethod.Patch, Delete = HttpMethod.Delete;
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-gha-tests-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;
    readonly FakeGitHub github = new();
    readonly GitHubActionsCi ci;

    public GitHubActionsCiTests()
    {
        store = new FileSystemArtifactStore(folder);
        ci = new GitHubActionsCi(github.Api(), () => store) { LostAnswerWindow = TimeSpan.FromMilliseconds(30), LostAnswerInterval = TimeSpan.FromMilliseconds(10) };
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

    static readonly string Claim = GitHubActionsCi.ClaimFor("key-1");
    const string TagObject = "7777777777777777777777777777777777777777";

    FakeGitHub Dispatchable(string workflow = UnitWorkflow, int dispatchStatus = 200) =>
        github.On(Get, "~/contents/.github/workflows/unit.yml?ref=main", 200, File64(workflow))
            .On(Post, "~/actions/workflows/unit.yml/dispatches", dispatchStatus, dispatchStatus == 200 ? """{"workflow_run_id":42,"html_url":"https://x/42"}""" : "")
            .On(Post, "~/git/tags", 201, $$$"""{"sha":"{{{TagObject}}}"}""")
            .On(Patch, $"~/git/refs/feature-flow/ci/{Claim}", 200, "{}")
            .On(Get, "~", 200, """{"default_branch":"main"}""");

    [Fact]
    public async Task A_trigger_claims_the_key_and_dispatches_on_the_default_branch_with_the_revision_as_input()
    {
        github.On(Post, "~/git/refs", 201, "{}");
        Dispatchable();

        var run = (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap();

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), run);
        var refs = github.Called(Post, "~/git/refs").Select(c => JsonNode.Parse(c.Body)!).ToList();
        Assert.Equal(("refs/feature-flow/ci/" + Claim, Sha), (refs[0]["ref"]!.GetValue<string>(), refs[0]["sha"]!.GetValue<string>()));
        var dispatch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches")).Body)!;
        // The workflow runs from the reviewed default branch, never from the revision the agent made.
        Assert.Equal("main", dispatch["ref"]!.GetValue<string>());
        Assert.True(dispatch["return_run_details"]!.GetValue<bool>());
        Assert.Equal(("unit", Sha, "key-1"),
            (dispatch["inputs"]!["tier"]!.GetValue<string>(), dispatch["inputs"]!["commit_sha"]!.GetValue<string>(), dispatch["inputs"]!["idempotency_key"]!.GetValue<string>()));
        // The run is recorded on the claim, and indexed by its id for clean-up.
        var tag = JsonNode.Parse(Assert.Single(github.Called(Post, "~/git/tags")).Body)!;
        Assert.Equal((Claim, "ff-ci run 42 https://x/42", Sha), (tag["tag"]!.GetValue<string>(), tag["message"]!.GetValue<string>(), tag["object"]!.GetValue<string>()));
        Assert.Equal(TagObject, JsonNode.Parse(Assert.Single(github.Called(Patch, $"~/git/refs/feature-flow/ci/{Claim}")).Body)!["sha"]!.GetValue<string>());
        Assert.Equal(("refs/feature-flow/ci/run-42", TagObject), (refs[1]["ref"]!.GetValue<string>(), refs[1]["sha"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_retried_trigger_adopts_the_run_its_claim_records_without_dispatching()
    {
        github.On(Post, "~/git/refs", 422, """{"message":"Reference already exists"}""")
            .On(Get, $"~/git/ref/feature-flow/ci/{Claim}", 200, $$$"""{"object":{"type":"tag","sha":"{{{TagObject}}}"}}""")
            .On(Get, $"~/git/tags/{TagObject}", 200, $$$"""{"tag":"{{{Claim}}}","message":"ff-ci run 42 https://x/42\n","object":{"sha":"{{{Sha}}}"}}""");

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap());
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_claim_with_no_run_recorded_adopts_the_run_whose_run_name_carries_the_key()
    {
        // The dispatch's answer was lost before the run was recorded; the workflow shows the key in its run-name.
        github.On(Get, $"~/git/ref/feature-flow/ci/{Claim}", 200, $$$"""{"object":{"type":"commit","sha":"{{{Sha}}}"}}""")
            .On(Post, "~/git/refs", 422, """{"message":"Reference already exists"}""")
            .On(Get, "~/actions/workflows/unit.yml/runs?event=workflow_dispatch&branch=main", 200,
                """{"workflow_runs":[{"id":41,"display_title":"ff-contract key-10"},{"id":42,"display_title":"ff-contract key-1","html_url":"https://x/42"}]}""");
        Dispatchable();

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap());
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
        Assert.Single(github.Called(Post, "~/git/tags"));
    }

    [Fact]
    public async Task A_claim_with_no_run_recorded_dispatches_again_when_no_run_carries_the_key()
    {
        // The rare worst case: a duplicate run, which is harmless.
        github.On(Get, $"~/git/ref/feature-flow/ci/{Claim}", 200, $$$"""{"object":{"type":"commit","sha":"{{{Sha}}}"}}""")
            .On(Get, "~/actions/workflows/unit.yml/runs?event=workflow_dispatch&branch=main", 200, """{"workflow_runs":[{"id":41,"display_title":"unit"}]}""");
        var posts = 0;
        github.On(Post, "~/git/refs", _ => new HttpResponseMessage(posts++ == 0 ? System.Net.HttpStatusCode.UnprocessableEntity : System.Net.HttpStatusCode.Created)
            { Content = new StringContent("""{"message":"Reference already exists"}""") });
        Dispatchable();

        Assert.Equal("42", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tag")]
    public async Task A_key_reused_at_another_revision_is_refused(string type)
    {
        var other = new string('b', 40);
        github.On(Post, "~/git/refs", 422, """{"message":"Reference already exists"}""")
            .On(Get, $"~/git/ref/feature-flow/ci/{Claim}", 200, $$$"""{"object":{"type":"{{{type}}}","sha":"{{{(type == "tag" ? TagObject : other)}}}"}}""")
            .On(Get, $"~/git/tags/{TagObject}", 200, $$$"""{"tag":"{{{Claim}}}","message":"ff-ci run 42","object":{"sha":"{{{other}}}"}}""");

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_workflow_without_a_commit_sha_input_is_refused_because_it_would_build_the_default_branch()
    {
        github.On(Post, "~/git/refs", 201, "{}");
        Dispatchable("on: workflow_dispatch\njobs: {}\n");

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_dispatch_answered_without_the_run_is_unexpected()
    {
        github.On(Post, "~/git/refs", 201, "{}");
        Dispatchable(dispatchStatus: 204);

        Assert.Equal("unexpected_response", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
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
        github.On(Get, "~/actions/runs/42", 200, new JsonObject { ["status"] = status, ["conclusion"] = conclusion }.ToJsonString())
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 404, """{"message":"Not Found"}""");
        Assert.Equal(new CiRunStatus(state, expected), (await ci.GetRunStatusAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap());
    }

    [Fact]
    public async Task A_status_poll_that_sees_the_run_completed_removes_its_claim_and_one_that_does_not_leaves_it()
    {
        github.On(Get, "~/actions/runs/42", 200, """{"status":"completed","conclusion":"success"}""")
            .On(Get, "~/actions/runs/43", 200, """{"status":"in_progress","conclusion":null}""")
            .On(Get, "~/actions/runs/44", 200, """{"status":"completed","conclusion":"success"}""")
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 200, $$$"""{"object":{"type":"tag","sha":"{{{TagObject}}}"}}""")
            .On(Get, "~/git/ref/feature-flow/ci/run-44", 404, """{"message":"Not Found"}""")
            .On(Get, $"~/git/tags/{TagObject}", 200, $$$"""{"tag":"{{{Claim}}}","message":"ff-ci run 42"}""")
            .On(Delete, "~/git/refs/feature-flow/ci/", 204);

        (await ci.GetRunStatusAsync(new CiRunRef(Repo, "43", Sha), Ct)).Unwrap();
        (await ci.GetRunStatusAsync(new CiRunRef(Repo, "44", Sha), Ct)).Unwrap();
        Assert.Empty(github.Called(Delete, "~/git/refs"));
        Assert.Equal(CheckStates.Completed, (await ci.GetRunStatusAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap().State);
        Assert.Equal([$"/repos/o/r/git/refs/feature-flow/ci/{Claim}", "/repos/o/r/git/refs/feature-flow/ci/run-42"],
            github.Called(Delete, "~/git/refs").Select(c => c.Path));
    }

    [Fact]
    public async Task Only_a_full_commit_SHA_and_a_numeric_run_id_reach_a_URL()
    {
        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, "main", "key-1", Ct)).Error?.Reason);
        Assert.Equal("validation_failed", (await ci.GetExpectedRunsAsync(Repo, "../x", null, Ct)).Error?.Reason);
        Assert.Equal("not_found", (await ci.GetRunStatusAsync(new CiRunRef(Repo, "1/../2", Sha), Ct)).Error?.Reason);
        Assert.Equal("not_found", (await ci.ReadLogsAsync(new CiRunRef(Repo, "x", Sha), Ct)).Error?.Reason);
        Assert.Empty(github.Calls);
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
    public async Task Results_are_the_jobs_and_the_result_files_from_the_artifacts()
    {
        github.On(Get, "~/actions/runs/42/jobs", 200, """{"jobs":[{"name":"unit","conclusion":"success"}]}""")
            .On(Get, "~/actions/runs/42/artifacts", 200, """{"artifacts":[{"id":5,"name":"unit-results","expired":false},{"id":6,"name":"old","expired":true}]}""")
            .On(Get, "~/actions/runs/42", 200, """{"status":"completed","conclusion":"success","head_branch":"main"}""")
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 404, """{"message":"Not Found"}""")
            .On(Get, "~/actions/artifacts/5/zip", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/5.zip" })
            .Bytes("https://blob.example/5.zip", Zip(("unit.trx", "<TestRun />"), ("junit/results.xml", "<?xml version=\"1.0\"?><testsuites/>"),
                ("analysis.sarif", "{}"), ("notes.txt", "x"), ("other.xml", "<project/>")))
;

        var result = (await ci.ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap();

        Assert.Equal(Conclusions.Success, result.Conclusion);
        Assert.Equal([new CiJobResult("unit", Conclusions.Success)], result.Jobs);
        Assert.Equal([("unit-results/unit.trx", "trx"), ("unit-results/junit/results.xml", "junit"), ("unit-results/analysis.sarif", "sarif")],
            result.Files.Select(f => (f.Name, f.Format)));
        await using var trx = (await store.GetAsync(result.Files[0].Content, Ct)).Unwrap();
        Assert.Equal("<TestRun />", await new StreamReader(trx).ReadToEndAsync(Ct));
        Assert.Empty(github.Called(Get, "~/actions/artifacts/6"));
        // The download carries no GitHub token.
        Assert.DoesNotContain(github.Calls, c => c.Path.StartsWith("https://blob.example", StringComparison.Ordinal) && c.Body.Contains("ghs_", StringComparison.Ordinal));
    }

    GitHubActionsCi Capped() => new(github.Api(), () => store) { MaxArtifactBytes = 4000, MaxResultFileBytes = 1000 };

    FakeGitHub CompletedRun(string artifacts) =>
        github.On(Get, "~/actions/runs/42/jobs", 200, """{"jobs":[]}""")
            .On(Get, "~/actions/runs/42/artifacts", 200, artifacts)
            .On(Get, "~/actions/runs/42", 200, """{"status":"completed","conclusion":"success"}""")
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 404, """{"message":"Not Found"}""");

    /// <summary>Review finding 6: artifacts are size-capped, and only result-file entries are read, each capped.</summary>
    [Fact]
    public async Task An_artifact_over_the_size_limit_is_skipped_unread()
    {
        CompletedRun("""{"artifacts":[{"id":5,"name":"big-build","expired":false,"size_in_bytes":999999}]}""");

        var result = (await Capped().ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Unwrap();

        Assert.Empty(result.Files);
        Assert.Empty(github.Called(Get, "~/actions/artifacts/5"));
    }

    [Fact]
    public async Task A_download_larger_than_it_said_is_refused()
    {
        CompletedRun("""{"artifacts":[{"id":5,"name":"lies","expired":false,"size_in_bytes":10}]}""")
            .On(Get, "~/actions/artifacts/5/zip", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/5.zip" })
            .Bytes("https://blob.example/5.zip", new byte[5000]);

        Assert.Equal("validation_failed", (await Capped().ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_result_file_over_the_limit_refuses_the_read_and_other_large_files_are_not_read()
    {
        CompletedRun("""{"artifacts":[{"id":5,"name":"results","expired":false,"size_in_bytes":3000}]}""")
            .On(Get, "~/actions/artifacts/5/zip", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/5.zip" })
            .Bytes("https://blob.example/5.zip", Zip(("app.dll", new string('x', 50_000)), ("unit.trx", new string('y', 2000))));

        var result = await Capped().ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct);

        Assert.Equal("validation_failed", result.Error?.Reason);
        Assert.Contains("unit.trx", result.Error!.Message);
    }

    [Fact]
    public async Task A_capped_copy_stops_at_the_limit_whatever_the_source_claims()
    {
        var target = new MemoryStream();
        Assert.False(await GitHubApi.CopyCappedAsync(new MemoryStream(new byte[1001]), target, 1000, Ct));
        Assert.True(target.Length <= 1000);
        Assert.True(await GitHubApi.CopyCappedAsync(new MemoryStream(new byte[1000]), new MemoryStream(), 1000, Ct));
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
