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
        Assert.Equal(bad, (await SubmitAsync(http, sid, bad))["revision"]!.ToString());

        var second = await OpenSessionAsync(h, http, id, after: sid);
        var view = await h.ShowAsync(id);
        Assert.Equal(badCheck["check"]!["inputs"]!.ToString(), Digests(view, "check_requirements")[0]);
        Assert.Equal(Invalid, second["drafts"]!["requirements.yaml"]!.ToString());

        // Round 2: fixed, checked, submitted. The check at one commit and a submission of another are given different inputs.
        var sid2 = second["session"]!["sessionId"]!.ToString();
        var branch2 = second["workspace"]!["branch"]!.ToString();
        var good = Commit(forge, repo, branch2, Valid);
        var goodCheck = await CheckAsync(http, sid2, good);
        Assert.Equal("passed", goodCheck["check"]!["status"]!.ToString());
        Assert.Contains("\"valid\": true", goodCheck["result"]!.ToString());
        Assert.NotEqual(badCheck["check"]!["inputs"]!.ToString(), goodCheck["check"]!["inputs"]!.ToString());

        // The body cannot name another actor (F8): the person is the authenticated caller.
        var submitted = await PostAsync(http, $"sessions/{sid2}/submit", new JsonObject { ["revision"] = good, ["actor"] = "mallory" });
        Assert.Equal("recorded", submitted["result"]!.ToString());
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

        Assert.Equal("recorded", (await PostAsync(http, $"sessions/{sid}/abandon", new JsonObject()))["result"]!.ToString());
        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal($"run {id} rejected", end.Trim());
        var record = await GetAsync(http, $"sessions/{sid}");
        Assert.Equal(SessionStatuses.Closed, record["session"]!["status"]!.ToString());
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

    static Task<JsonNode> SubmitAsync(HttpClient http, string sid, string revision) => PostAsync(http, $"sessions/{sid}/submit", new JsonObject { ["revision"] = revision });

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
