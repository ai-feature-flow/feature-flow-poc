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
    const string ClaimTag = "7777777777777777777777777777777777777777";
    const string RecordTag = "8888888888888888888888888888888888888888";
    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The repository, the workflow on the default branch, the tag objects, the dispatch and the recording.</summary>
    FakeGitHub Dispatchable(string workflow = UnitWorkflow, int dispatchStatus = 200, int claimStatus = 201, string? committed = null)
    {
        var tags = 0;
        var refs = 0;
        return github.On(Get, "~/contents/.github/workflows/unit.yml?ref=main", 200, File64(workflow))
            .On(Post, "~/actions/workflows/unit.yml/dispatches", dispatchStatus,
                dispatchStatus == 200 ? """{"workflow_run_id":43,"html_url":"https://x/43"}""" : """{"message":"No ref found for: main"}""")
            .On(Post, "~/git/tags", _ => Json(201, $$$"""{"sha":"{{{(tags++ == 0 ? ClaimTag : RecordTag)}}}"}"""))
            .On(Post, "~/git/refs", _ => refs++ == 0 && claimStatus != 201 ? Json(claimStatus, """{"message":"Reference already exists"}""") : Json(201, "{}"))
            .On(Patch, $"~/git/refs/feature-flow/ci/{Claim}", 200, "{}")
            .On(Delete, "~/git/refs/feature-flow/ci/", 204)
            .On(Get, $"~/git/commits/{Sha}", 200, $$$"""{"committer":{"date":"{{{committed ?? Iso(DateTimeOffset.UtcNow.AddDays(-1))}}}"}}""");
    }

    static HttpResponseMessage Json(int status, string body) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };

    void Repository() => github.On(Get, "~", 200, """{"default_branch":"main"}""");

    /// <summary>An existing claim: a tag object at <paramref name="revision"/>, dated <paramref name="claimedAt"/>, recording <paramref name="message"/>.</summary>
    void ExistingClaim(DateTimeOffset claimedAt, string message, string? revision = null) =>
        github.On(Get, $"~/git/ref/feature-flow/ci/{Claim}", 200, $$$"""{"object":{"type":"tag","sha":"{{{ClaimTag}}}"}}""")
            .On(Get, $"~/git/tags/{ClaimTag}", 200,
                new JsonObject { ["tag"] = Claim, ["message"] = message, ["object"] = new JsonObject { ["sha"] = revision ?? Sha }, ["tagger"] = new JsonObject { ["date"] = Iso(claimedAt) } }.ToJsonString());

    static string Run(long id, DateTimeOffset created, string path = ".github/workflows/unit.yml", string @event = "workflow_dispatch", string branch = "main",
        string title = "unit") =>
        new JsonObject
        {
            ["id"] = id, ["path"] = path, ["event"] = @event, ["head_branch"] = branch, ["created_at"] = Iso(created), ["display_title"] = title,
            ["html_url"] = $"https://x/{id}",
        }.ToJsonString();

    [Fact]
    public async Task A_trigger_checks_first_then_claims_and_dispatches_on_the_default_branch_with_the_revision_as_input()
    {
        Dispatchable();
        Repository();

        var run = (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap();

        Assert.Equal(new CiRunRef(Repo, "43", Sha, "https://x/43"), run);
        // Review R1: everything that can refuse is read before the claim is made.
        var paths = github.Calls.Select(c => (c.Method, c.Path)).ToList();
        Assert.True(paths.FindIndex(c => c.Path.Contains("/contents/", StringComparison.Ordinal)) < paths.FindIndex(c => c.Method == Post && c.Path.EndsWith("/git/tags", StringComparison.Ordinal)));
        var tags = github.Called(Post, "~/git/tags").Select(c => JsonNode.Parse(c.Body)!).ToList();
        Assert.Equal((Claim, "ff-ci claim", Sha), (tags[0]["tag"]!.GetValue<string>(), tags[0]["message"]!.GetValue<string>(), tags[0]["object"]!.GetValue<string>()));
        var refs = github.Called(Post, "~/git/refs").Select(c => JsonNode.Parse(c.Body)!).ToList();
        Assert.Equal(("refs/feature-flow/ci/" + Claim, ClaimTag), (refs[0]["ref"]!.GetValue<string>(), refs[0]["sha"]!.GetValue<string>()));
        var dispatch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches")).Body)!;
        // The workflow runs from the reviewed default branch, never from the revision the agent made.
        Assert.Equal("main", dispatch["ref"]!.GetValue<string>());
        Assert.True(dispatch["return_run_details"]!.GetValue<bool>());
        Assert.Equal(("unit", Sha, "key-1"),
            (dispatch["inputs"]!["tier"]!.GetValue<string>(), dispatch["inputs"]!["commit_sha"]!.GetValue<string>(), dispatch["inputs"]!["idempotency_key"]!.GetValue<string>()));
        // The run is recorded on the claim, keeping the claim's date, and indexed by its id for clean-up.
        Assert.Equal(("ff-ci run 43 https://x/43", tags[0]["tagger"]!["date"]!.GetValue<string>()), (tags[1]["message"]!.GetValue<string>(), tags[1]["tagger"]!["date"]!.GetValue<string>()));
        Assert.Equal(RecordTag, JsonNode.Parse(Assert.Single(github.Called(Patch, $"~/git/refs/feature-flow/ci/{Claim}")).Body)!["sha"]!.GetValue<string>());
        Assert.Equal(("refs/feature-flow/ci/run-43", RecordTag), (refs[1]["ref"]!.GetValue<string>(), refs[1]["sha"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_retried_trigger_adopts_the_run_its_claim_records_once_it_checks_out()
    {
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        Dispatchable(claimStatus: 422);
        ExistingClaim(claimedAt, "ff-ci run 42 https://x/42");
        github.On(Get, "~/actions/runs/42", 200, Run(42, claimedAt.AddSeconds(5)));
        Repository();

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap());
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    /// <summary>
    /// Review R2: a recorded run is adopted only if it is what this trigger would have started. Anything that may write
    /// the claim could point it at another run; then the trigger dispatches its own, and drops the other's index.
    /// </summary>
    [Theory]
    [InlineData("path")]
    [InlineData("event")]
    [InlineData("branch")]
    [InlineData("before the claim")]
    [InlineData("before the commit")]
    public async Task A_recorded_run_that_does_not_check_out_is_not_adopted(string wrong)
    {
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var committed = wrong == "before the commit" ? claimedAt.AddMinutes(5) : claimedAt.AddDays(-1);
        Dispatchable(claimStatus: 422, committed: Iso(committed));
        ExistingClaim(claimedAt, "ff-ci run 42 https://x/42");
        github.On(Get, "~/actions/runs/42", 200, wrong switch
        {
            "path" => Run(42, claimedAt.AddSeconds(5), path: ".github/workflows/other.yml"),
            "event" => Run(42, claimedAt.AddSeconds(5), @event: "push"),
            "branch" => Run(42, claimedAt.AddSeconds(5), branch: "ff/agent"),
            "before the claim" => Run(42, claimedAt.AddDays(-30)),
            _ => Run(42, claimedAt.AddSeconds(5)),
        });
        Repository();

        Assert.Equal("43", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
        Assert.Single(github.Called(Delete, "~/git/refs/feature-flow/ci/run-42"));
    }

    [Fact]
    public async Task A_claim_with_no_run_recorded_adopts_the_run_whose_run_name_carries_the_key()
    {
        // The dispatch's answer was lost before the run was recorded; the workflow shows the key in its run-name.
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        Dispatchable(claimStatus: 422);
        ExistingClaim(claimedAt, "ff-ci claim");
        github.On(Get, "~/actions/workflows/unit.yml/runs?event=workflow_dispatch&branch=main", 200,
            $"{{\"workflow_runs\":[{Run(41, claimedAt.AddSeconds(5), title: "ff-contract key-10")},{Run(42, claimedAt.AddSeconds(5), title: "ff-contract key-1")}]}}");
        Repository();

        Assert.Equal(new CiRunRef(Repo, "42", Sha, "https://x/42"), (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap());
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
        Assert.Equal(2, github.Called(Post, "~/git/tags").Count());
    }

    [Fact]
    public async Task A_claim_with_no_run_recorded_dispatches_again_when_no_run_carries_the_key()
    {
        // The rare worst case: a duplicate run, which is harmless. A run with the key but older than the claim is not it.
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        Dispatchable(claimStatus: 422);
        ExistingClaim(claimedAt, "ff-ci claim");
        github.On(Get, "~/actions/workflows/unit.yml/runs?event=workflow_dispatch&branch=main", 200,
            $"{{\"workflow_runs\":[{Run(40, claimedAt.AddDays(-2), title: "ff-contract key-1")},{Run(41, claimedAt.AddSeconds(5))}]}}");
        Repository();

        Assert.Equal("43", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        Assert.Single(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_stale_claim_with_no_run_recorded_is_reclaimed_without_searching()
    {
        Dispatchable(claimStatus: 422);
        ExistingClaim(DateTimeOffset.UtcNow.AddHours(-4), "ff-ci claim");
        Repository();

        Assert.Equal("43", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Unwrap().Id);
        Assert.Empty(github.Called(Get, "~/actions/workflows/unit.yml/runs"));
        var record = JsonNode.Parse(github.Called(Post, "~/git/tags").Last().Body)!;
        Assert.True(DateTimeOffset.Parse(record["tagger"]!["date"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_key_reused_at_another_revision_is_refused()
    {
        Dispatchable(claimStatus: 422);
        ExistingClaim(DateTimeOffset.UtcNow, "ff-ci run 42", revision: new string('b', 40));
        Repository();

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
        Assert.Empty(github.Called(Post, "~/actions/workflows/unit.yml/dispatches"));
    }

    [Fact]
    public async Task A_workflow_without_a_commit_sha_input_is_refused_before_anything_is_claimed()
    {
        Dispatchable("on: workflow_dispatch\njobs: {}\n");
        Repository();

        Assert.Equal("validation_failed", (await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Error?.Reason);
        Assert.Empty(github.Called(Post, "~/"));
    }

    /// <summary>Review R1: a dispatch refused as a work error started no run, so its claim is removed; any other failure may have started one.</summary>
    [Theory]
    [InlineData(422, true)]
    [InlineData(500, false)]
    public async Task A_refused_dispatch_removes_its_claim_and_a_failed_one_keeps_it(int status, bool removed)
    {
        Dispatchable(dispatchStatus: status);
        Repository();

        Assert.False((await ci.TriggerAsync(Repo, Unit, Sha, "key-1", Ct)).Ok);
        Assert.Equal(removed ? 1 : 0, github.Called(Delete, $"~/git/refs/feature-flow/ci/{Claim}").Count());
    }

    [Fact]
    public async Task A_dispatch_answered_without_the_run_is_unexpected()
    {
        Dispatchable(dispatchStatus: 204);
        Repository();

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
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 200, $$$"""{"object":{"type":"tag","sha":"{{{ClaimTag}}}"}}""")
            .On(Get, "~/git/ref/feature-flow/ci/run-44", 404, """{"message":"Not Found"}""")
            .On(Get, $"~/git/tags/{ClaimTag}", 200, $$$"""{"tag":"{{{Claim}}}","message":"ff-ci run 42"}""")
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
            .On(Get, "~/git/ref/feature-flow/ci/run-42", 404, """{"message":"Not Found"}""")
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
