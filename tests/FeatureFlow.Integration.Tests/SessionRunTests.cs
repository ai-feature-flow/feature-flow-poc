using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M7's interactive sessions (docs/04 section 4.4) and done-when 2 on the in-memory world: std's <c>project_delivery</c>
/// reaches its requirements session; a person fetches it, commits to its branch, runs the check of <c>check_requirements</c>
/// before submitting, and submits; the run imports the files at that commit and proceeds. And the verified-by (docs/09
/// section 4.2): the pre-submit check and the later step receive the same task, tools, model and commit.
/// </summary>
[Trait("Category", "Integration")]
public class SessionRunTests
{
    const string Valid = """
        schema: 1
        scope: { kind: project, id: invoicing }
        requirements:
          - { id: R-1, title: Invoices are listed, behaviour: A created invoice appears in the list of invoices., supports_reason: The project's own behaviour }
        """;

    const string Invalid = """
        schema: 1
        scope: { kind: project, id: invoicing }
        requirements:
          - { id: R-1, title: Invoices are listed, behaviour: A created invoice appears in the list of invoices. }
        """;

    const string Architecture = """
        ---
        scope_kind: project
        scope_id: invoicing
        ---
        # Scope
        # Components and responsibilities
        # Contracts
        # Data and control flow
        # Failure handling
        # Decisions
        """;

    [Fact]
    public async Task A_person_checks_before_submitting_submits_and_the_run_proceeds_with_the_check_given_what_the_step_was()
    {
        var (h, forge, repo) = await ForgeTicketTests.StartAsync("session", autoApprove: false);
        await using var _h = h;
        var id = h.Start("std:templates/project_delivery", "--param", "project_name=invoicing", "--repo", repo.FullName);
        var http = await ClientAsync(h);

        // Round 1: the requirements do not pass the check before submission; the person submits anyway, and the later
        // step finds the same, so the run sends the person back to a new session seeded with what they submitted.
        var first = await OpenSessionAsync(h, http, id);
        var workspace = first["workspace"]!;
        Assert.Equal("projects/invoicing", workspace["folder"]!.ToString());
        Assert.Equal(["check_requirements"], workspace["checksFrom"]!.AsArray().Select(c => c!.ToString()));
        var branch = workspace["branch"]!.ToString();
        Assert.Equal(workspace["baseRevision"]!.ToString(), forge.Resolve(repo, "refs/heads/" + branch));
        Assert.Contains("ff session submit", workspace["instructions"]!.ToString());
        Assert.Contains("invoicing", workspace["prompt"]!.ToString());
        Assert.Empty(first["drafts"]!.AsObject());
        var sid = first["session"]!["sessionId"]!.ToString();

        var bad = Commit(forge, repo, branch, Invalid);
        var badCheck = await CheckAsync(http, sid, bad);
        Assert.Equal("failed", badCheck["check"]!["status"]!.ToString());
        Assert.Contains("invalid", badCheck["detail"]!.ToString());
        // The body cannot name another actor (F8): the person is the authenticated caller.
        var firstSubmit = await PostAsync(http, $"sessions/{sid}/submit", new JsonObject { ["revision"] = bad, ["actor"] = "mallory" });
        Assert.Equal(bad, firstSubmit["revision"]!.ToString());
        Assert.Equal("tester", (await GetAsync(http, $"sessions/{sid}"))["session"]!["person"]!.ToString());

        var second = await OpenSessionAsync(h, http, id, after: sid);
        var view = await h.ShowAsync(id);
        Assert.Equal(badCheck["check"]!["inputs"]!.ToString(), Digests(view, "check_requirements")[0]);
        Assert.Equal(Invalid, second["drafts"]!["requirements.yaml"]!.ToString());

        // Round 2, as a person does it: ff session fetch, the check through the engine MCP server, ff session submit.
        var sid2 = second["session"]!["sessionId"]!.ToString();
        var branch2 = second["workspace"]!["branch"]!.ToString();
        var folder = Directory.CreateTempSubdirectory("ff-session-").FullName;
        var (fetchExit, fetched, fetchError) = Session(h, "fetch", sid2, "--out", folder);
        Assert.True(fetchExit == 0, fetchError);
        Assert.Contains($"branch {branch2}", fetched);
        Assert.Equal(Invalid, await File.ReadAllTextAsync(Path.Combine(folder, "drafts", "requirements.yaml"), TestContext.Current.CancellationToken));
        Assert.Contains("invoicing", await File.ReadAllTextAsync(Path.Combine(folder, "prompt.md"), TestContext.Current.CancellationToken));
        Directory.Delete(folder, recursive: true);

        var good = Commit(forge, repo, branch2, Valid);
        var replies = Mcp(h,
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" },
            new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "session_check", ["arguments"] = new JsonObject { ["session_id"] = sid2, ["step"] = "check_requirements", ["revision"] = good } },
            });
        Assert.Equal(["session_fetch", "session_check", "session_submit"], replies[1]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()));
        var checkText = replies[2]["result"]!["content"]![0]!["text"]!.ToString();
        Assert.False(replies[2]["result"]!["isError"]!.GetValue<bool>(), checkText);
        Assert.Contains($"at {good}: passed", checkText);
        var goodCheck = (await GetAsync(http, $"sessions/{sid2}/checks"))["checks"]!.AsArray().Single()!;
        Assert.Contains("\"valid\": true", goodCheck["result"]!.ToString());
        // The check at one commit and at another are given different inputs.
        Assert.NotEqual(badCheck["check"]!["inputs"]!.ToString(), goodCheck["check"]!["inputs"]!.ToString());

        var (submitExit, submittedText, submitError) = Session(h, "submit", sid2, "--revision", good);
        Assert.True(submitExit == 0, submitError);
        Assert.Contains($"submitted {good} as submitted: recorded", submittedText);
        await WaitAtAsync(h, id, "approve");

        view = await h.ShowAsync(id);
        var digests = Digests(view, "check_requirements");
        Assert.Equal(2, digests.Count);
        Assert.Equal(goodCheck["check"]!["inputs"]!.ToString(), digests[1]);
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == "check_requirements"
                                                          && e["detail"]!.ToString() == "valid");

        var record = await GetAsync(http, $"sessions/{sid2}");
        Assert.Equal(SessionStatuses.Submitted, record["session"]!["status"]!.ToString());
        Assert.Equal("tester", record["session"]!["person"]!.ToString());
        Assert.Equal(good, record["session"]!["submittedRevision"]!.ToString());
        Assert.Equal(["contract", "requirements"], record["session"]!["artifacts"]!.AsObject().Select(kv => kv.Key).Order());
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "session_submitted" && e["detail"]!.ToString().Contains($"at {good} by tester"));

        // A closed session takes no more checks or submissions.
        var late = await http.PostAsJsonAsync($"sessions/{sid2}/checks", new { step = "check_requirements", revision = good }, TestContext.Current.CancellationToken);
        Assert.Equal(409, (int)late.StatusCode);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await PostAsync(http, $"runs/{id}/abort", new JsonObject { ["reason"] = "the test is done" });
        h.Cli("wait", id, "--timeout", "60");
        // A history holding the m7-sessions marker: opening, checks, submissions and the step's digest (docs/09 section 4.1).
        await h.RecordHistoryAsync(id, "project_session");
    }

    [Fact]
    public async Task A_plain_answer_cannot_take_the_outcome_that_imports_files_and_abandon_closes_the_session()
    {
        var (h, _, repo) = await ForgeTicketTests.StartAsync("session-abandon", autoApprove: false);
        await using var _h = h;
        var id = h.Start("std:templates/project_delivery", "--param", "project_name=invoicing", "--repo", repo.FullName);
        var http = await ClientAsync(h);
        var opened = await OpenSessionAsync(h, http, id);
        var sid = opened["session"]!["sessionId"]!.ToString();
        var wait = opened["session"]!["waitId"]!.ToString();

        var (exit, _, error) = h.Cli("answer", id, wait, "submitted");
        Assert.NotEqual(0, exit);
        Assert.Contains("submit it (ff session submit", error);
        var notCommit = await http.PostAsJsonAsync($"sessions/{sid}/submit", new { revision = "main" }, TestContext.Current.CancellationToken);
        Assert.Equal(400, (int)notCommit.StatusCode);
        var notACheck = await http.PostAsJsonAsync($"sessions/{sid}/checks", new { step = "approve", revision = new string('a', 40) }, TestContext.Current.CancellationToken);
        Assert.Equal(409, (int)notACheck.StatusCode);
        Assert.Contains("not a check of session", await notACheck.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The run's own refusals, for an update that does not come through the API (F8 of the review).
        var handle = h.Client.GetWorkflowHandle(id);
        var commit = new string('a', 40);
        foreach (var (args, error_) in new[]
                 {
                     (new AnswerArgs(wait, "tester", "submitted", null, Revision: "main"), "full commit id"),
                     (new AnswerArgs(wait, "tester", "submitted", null, Revision: commit, Artifacts: new Dictionary<string, string> { ["requirements"] = "sha256:x", ["contract"] = "sha256:y" }),
                         "imported by the run"),
                     (new AnswerArgs(wait, "tester", "abandoned", null, Revision: commit), "a submission takes 'submitted'"),
                 })
        {
            var refusal = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowUpdateFailedException>(() => handle.ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [args]));
            Assert.Contains(error_, refusal.InnerException?.Message ?? refusal.Message);
        }

        Assert.Equal("recorded", (await PostAsync(http, $"sessions/{sid}/abandon", new JsonObject()))["result"]!.ToString());
        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal($"run {id} rejected", end.Trim());
        var record = await GetAsync(http, $"sessions/{sid}");
        Assert.Equal(SessionStatuses.Closed, record["session"]!["status"]!.ToString());
    }

    [Fact]
    public async Task A_session_carried_over_a_hand_over_still_takes_a_submission_and_another_wait_refuses_one()
    {
        var (h, forge, repo) = await ForgeTicketTests.StartAsync("session-carry", autoApprove: false);
        await using var _h = h;
        var id = h.Start("std:templates/project_delivery", "--param", "project_name=invoicing", "--repo", repo.FullName, "--continue-as-new-events", "100");
        var http = await ClientAsync(h);
        var opened = await OpenSessionAsync(h, http, id);
        var sid = opened["session"]!["sessionId"]!.ToString();
        var good = Commit(forge, repo, opened["workspace"]!["branch"]!.ToString(), Valid);

        // Checks grow the history until the run hands over with the session's decision open.
        for (var i = 0; i < 20 && (await h.RunsOfAsync(id)).Count < 2; i++)
            await CheckAsync(http, sid, good);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while ((await h.RunsOfAsync(id)).Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True((await h.RunsOfAsync(id)).Count >= 2, "the run did not hand over");

        Assert.Equal("recorded", (await PostAsync(http, $"sessions/{sid}/submit", new JsonObject { ["revision"] = good }))["result"]!.ToString());
        await WaitAtAsync(h, id, "approve");

        // A revision on a wait that is not a session is refused, never taken as a plain answer (fails closed).
        var approve = (await h.ShowAsync(id))["waits"]![0]!["waitId"]!.ToString();
        var refusal = await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowUpdateFailedException>(() =>
            h.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [new AnswerArgs(approve, "tester", "approved", null, Revision: good)]));
        Assert.Contains("is not an open session", refusal.InnerException?.Message ?? refusal.Message);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await PostAsync(http, $"runs/{id}/abort", new JsonObject { ["reason"] = "the test is done" });
    }

    [Fact]
    public async Task A_check_runs_only_an_artifact_validate_step_even_when_checks_from_names_another()
    {
        var (h, _, repo) = await ForgeTicketTests.StartAsync("session-publish", autoApprove: false, libraries: [Path.Combine(Harness.TestLibraries, "m7")]);
        await using var _h = h;
        var id = h.Start("m7:templates/session_checks", "--repo", repo.FullName);
        var http = await ClientAsync(h);
        var sid = (await OpenSessionAsync(h, http, id))["session"]!["sessionId"]!.ToString();

        var refused = await http.PostAsJsonAsync($"sessions/{sid}/checks", new { step = "publish", revision = new string('a', 40) }, TestContext.Current.CancellationToken);
        Assert.Equal(409, (int)refused.StatusCode);
        Assert.Contains("only an artifact.validate step", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await PostAsync(http, $"runs/{id}/abort", new JsonObject { ["reason"] = "the test is done" });
    }

    [Fact]
    public async Task A_dry_run_opens_no_session_and_its_decision_takes_a_plain_answer()
    {
        var (h, _, repo) = await ForgeTicketTests.StartAsync("session-dry", autoApprove: false);
        await using var _h = h;
        var id = h.Start("std:templates/project_delivery", "--param", "project_name=invoicing", "--repo", repo.FullName, "--dry-run");
        var http = await ClientAsync(h);
        await WaitAtAsync(h, id, "define");
        var wait = (await h.ShowAsync(id))["waits"]![0]!["waitId"]!.ToString();

        var (exit, output, error) = h.Cli("answer", id, wait, "submitted");
        Assert.True(exit == 0, output + error);
        // As before sessions: the plain answer produces no files, so the next step that consumes them stops at check 32 (LOG 245).
        await WaitAtAsync(h, id, "check_requirements");
        Assert.Empty((await GetAsync(http, $"runs/{id}/sessions"))["sessions"]!.AsArray());
        await PostAsync(http, $"runs/{id}/abort", new JsonObject { ["reason"] = "the test is done" });
    }

    static async Task WaitAtAsync(Harness h, string runId, string step)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if ((await h.ShowAsync(runId))["waits"]!.AsArray().Any(w => w!["stepId"]!.ToString() == step))
                return;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{runId} opened no wait at {step}: {(await h.ShowAsync(runId)).ToJsonString()}");
    }

    static List<string> Digests(JsonNode view, string step) =>
        view["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "check_inputs" && e["stepId"]!.ToString() == step).Select(e => e!["detail"]!.ToString()).ToList();

    static string Commit(InMemoryForge forge, RepoRef repo, string branch, string requirements) =>
        forge.Commit(repo, "refs/heads/" + branch, new Dictionary<string, string?>
        {
            ["projects/invoicing/requirements.yaml"] = requirements,
            ["projects/invoicing/architecture.md"] = Architecture,
        }, "the person's requirements", actor: "a-person");

    static async Task<HttpClient> ClientAsync(Harness h)
    {
        var http = new HttpClient { BaseAddress = new Uri(h.Api.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", await File.ReadAllTextAsync(h.TokenFile));
        return http;
    }

    /// <summary>Waits for the run's session (one other than <paramref name="after"/>) to have its workspace, and fetches it.</summary>
    static async Task<JsonNode> OpenSessionAsync(Harness h, HttpClient http, string runId, string? after = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var sessions = (await GetAsync(http, $"runs/{runId}/sessions"))["sessions"]!.AsArray()
                .Where(s => s!["status"]!.ToString() == SessionStatuses.Prepared && s["sessionId"]!.ToString() != after).ToList();
            if (sessions.Count == 1)
            {
                var sid = sessions[0]!["sessionId"]!.ToString();
                var fetched = await http.GetAsync($"sessions/{sid}", TestContext.Current.CancellationToken);
                if (fetched.IsSuccessStatusCode)
                    return JsonNode.Parse(await fetched.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{runId} opened no session: {(await h.ShowAsync(runId)).ToJsonString()}");
    }

    /// <summary>Runs a check of <c>check_requirements</c> at <paramref name="revision"/> and waits for its result.</summary>
    static async Task<JsonNode> CheckAsync(HttpClient http, string sid, string revision)
    {
        var started = await PostAsync(http, $"sessions/{sid}/checks", new JsonObject { ["step"] = "check_requirements", ["revision"] = revision });
        var checkId = started["check_id"]!.ToString();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var check = (await GetAsync(http, $"sessions/{sid}/checks"))["checks"]!.AsArray().FirstOrDefault(c => c!["check"]!["checkId"]!.ToString() == checkId);
            if (check is not null && check["check"]!["status"]!.ToString() != "running" && check["detail"] is not null)
                return check;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"check {checkId} of {sid} did not end");
    }

    /// <summary>Runs <c>ff session ...</c> in this process against this engine's API.</summary>
    static (int Exit, string Output, string Error) Session(Harness h, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = FeatureFlow.Cli.CliApp.Run(["session", .. args, "--api", h.Api, "--token-file", h.TokenFile], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Sends JSON-RPC messages to <c>ff session mcp</c> on its stdin and returns its replies.</summary>
    static List<JsonNode> Mcp(Harness h, params JsonObject[] messages)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader(string.Join("\n", messages.Select(m => m.ToJsonString())) + "\n");
        var exit = FeatureFlow.Cli.SessionCommands.RunAsync(["mcp", "--api", h.Api, "--token-file", h.TokenFile], output, error, input).GetAwaiter().GetResult();
        Assert.True(exit == 0, error.ToString());
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
    }

    static async Task<JsonNode> GetAsync(HttpClient http, string path)
    {
        var response = await http.GetAsync(path, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    static async Task<JsonNode> PostAsync(HttpClient http, string path, JsonObject body)
    {
        var response = await http.PostAsync(path, new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"POST {path}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }
}
