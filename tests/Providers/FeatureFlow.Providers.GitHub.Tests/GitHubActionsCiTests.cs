using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>The GitHub Actions provider's mapping of GitHub's responses, and its reading of workflow triggers, without the network.</summary>
public sealed class GitHubActionsCiTests : IDisposable
{
    static readonly RepoRef Repo = new("o/r");
    static readonly string Sha = new('a', 40);
    static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post;
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-gha-tests-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;
    readonly FakeGitHub github = new();
    readonly GitHubActionsCi ci;

    public GitHubActionsCiTests()
    {
        store = new FileSystemArtifactStore(folder);
        ci = new GitHubActionsCi(github.Api(), () => store);
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
          push:
            branches: [ main, "ff/**" ]
        jobs: {}
        """;

    static string File64(string text) =>
        new JsonObject { ["type"] = "file", ["encoding"] = "base64", ["size"] = text.Length, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }.ToJsonString();

    static string Runs(params (long Id, string Sha)[] runs) =>
        new JsonObject { ["workflow_runs"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["head_sha"] = r.Sha, ["html_url"] = $"https://x/{r.Id}" }).ToArray()) }
            .ToJsonString();

    /// <summary>The workflow on the default branch, the dispatch, and the repository (last: routes match by prefix, first first).</summary>
    FakeGitHub Dispatchable(string workflow = UnitWorkflow, int dispatchStatus = 200) =>
        github.On(Get, "~/contents/.github/workflows/unit.yml?ref=main", 200, File64(workflow))
            .On(Post, "~/actions/workflows/unit.yml/dispatches", dispatchStatus,
                dispatchStatus == 200 ? """{"workflow_run_id":43,"html_url":"https://x/43"}""" : """{"message":"No ref found for: main"}""")
            .On(Get, "~", 200, """{"default_branch":"main"}""");

    [Fact]
    public async Task A_trigger_dispatches_on_the_default_branch_with_the_revision_as_input_and_returns_the_run()
    {
        Dispatchable();

        var run = (await ci.TriggerAsync(Repo, Unit, Sha, Ct)).Unwrap();

        Assert.Equal(new CiRunRef(Repo, "43", Sha, "https://x/43"), run);
        var dispatch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches")).Body)!;
        // The workflow runs from the reviewed default branch, never from the revision the agent made.
        Assert.Equal("main", dispatch["ref"]!.GetValue<string>());
        Assert.True(dispatch["return_run_details"]!.GetValue<bool>());
        Assert.Equal(("unit", Sha), (dispatch["inputs"]!["tier"]!.GetValue<string>(), dispatch["inputs"]!["commit_sha"]!.GetValue<string>()));
        // Nothing but the dispatch is written: no claim or record (LOG 147).
        Assert.Single(github.Calls, c => c.Method != Get);
    }

    [Fact]
    public async Task A_workflow_without_a_commit_sha_input_is_refused_before_anything_is_dispatched()
    {
        Dispatchable("on: workflow_dispatch\njobs: {}\n");

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, Ct)).Error?.Reason);
        Assert.Empty(github.Called(Post, "~/"));
    }

    [Fact]
    public async Task A_refused_dispatch_is_its_error()
    {
        Dispatchable(dispatchStatus: 422);

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_dispatch_answered_without_the_run_is_unexpected()
    {
        Dispatchable(dispatchStatus: 204);

        Assert.Equal("unexpected_response", (await ci.TriggerAsync(Repo, Unit, Sha, Ct)).Error?.Reason);
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
    public async Task Only_a_full_commit_SHA_and_a_numeric_run_id_reach_a_URL()
    {
        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, "main", Ct)).Error?.Reason);
        Assert.Equal("validation_failed", (await ci.HasCiAsync(Repo, "../x", Ct)).Error?.Reason);
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
            .On(Get, "~/actions/runs/42", 200, """{"status":"completed","conclusion":"success"}""");

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

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("socket")]
    public async Task A_download_that_breaks_is_unreachable_not_an_exception(string kind)
    {
        github.On(Get, "~/actions/runs/42/logs", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/logs.zip" })
            .On(Get, "https://blob.example/logs.zip", _ => throw GitHubCodeHostTests.Transport(kind));

        var logs = await ci.ReadLogsAsync(new CiRunRef(Repo, "42", Sha), Ct);

        Assert.Equal("unreachable", logs.Error?.Reason);
    }

    [Fact]
    public async Task An_artifact_that_is_not_a_zip_is_refused_not_an_exception()
    {
        github.On(Get, "~/actions/runs/42/jobs", 200, """{"jobs":[]}""")
            .On(Get, "~/actions/runs/42/artifacts", 200, """{"artifacts":[{"id":5,"name":"broken","expired":false,"size_in_bytes":10}]}""")
            .On(Get, "~/actions/runs/42", 200, """{"status":"completed","conclusion":"success"}""")
            .On(Get, "~/actions/artifacts/5/zip", 302, "", new Dictionary<string, string> { ["Location"] = "https://blob.example/5.zip" })
            .Bytes("https://blob.example/5.zip", [1, 2, 3, 4, 5]);

        Assert.Equal("validation_failed", (await ci.ReadResultsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_logs_answer_that_is_not_a_redirect_is_unexpected()
    {
        github.On(Get, "~/actions/runs/42/logs", 200, "{}");
        Assert.Equal("unexpected_response", (await ci.ReadLogsAsync(new CiRunRef(Repo, "42", Sha), Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_revision_with_a_workflow_file_has_CI_and_one_without_has_none()
    {
        var other = new string('b', 40);
        github.On(Get, $"~/contents/.github/workflows?ref={Sha}", 200, """[{"name":"README.md","type":"file"},{"name":"docs.yaml","type":"file"}]""")
            .On(Get, $"~/contents/.github/workflows?ref={other}", 200, """[{"name":"README.md","type":"file"},{"name":"old","type":"dir"}]""");

        Assert.True((await ci.HasCiAsync(Repo, Sha, Ct)).Unwrap());
        Assert.False((await ci.HasCiAsync(Repo, other, Ct)).Unwrap());
    }

    [Fact]
    public async Task A_revision_without_a_workflow_folder_has_no_CI()
    {
        github.On(Get, $"~/contents/.github/workflows?ref={Sha}", 404, """{"message":"Not Found"}""");
        Assert.False((await ci.HasCiAsync(Repo, Sha, Ct)).Unwrap());
    }

    [Theory]
    [InlineData("on: { workflow_dispatch: { inputs: { commit_sha: { type: string }, tier: {} } } }\njobs: {}\n", "commit_sha,tier")]
    [InlineData("on: [push, workflow_dispatch]\njobs: {}\n", "")]
    [InlineData("on: [push\n", "")]
    public void Dispatch_inputs_are_read_from_the_workflow(string text, string inputs)
    {
        Assert.Equal(inputs, string.Join(",", GitHubActionsCi.DispatchInputs(text).Order(StringComparer.Ordinal)));
    }
}
