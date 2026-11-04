using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// A session's endpoints authorise the authenticated caller (F8), whoever started the run: acting on a session needs a
/// role that may answer its decision, and is refused before anything is recorded or sent; a closed session takes nothing.
/// </summary>
public sealed class SessionApiTests : IAsyncDisposable
{
    const string Sid = "r1.decision-1";
    const string Commit = "0123456789abcdef0123456789abcdef01234567";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-api-session-" + Guid.NewGuid().ToString("N"));
    readonly List<Microsoft.AspNetCore.Builder.WebApplication> apps = [];
    readonly SqliteRecordStore records;

    public SessionApiTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    /// <summary>A person other than the run's starter, by header, with <paramref name="roles"/>; the local token is the starter's.</summary>
    sealed class PersonAuthenticator(params string[] roles) : IAuthenticator
    {
        public Task<Caller?> AuthenticateAsync(Microsoft.AspNetCore.Http.HttpContext context, ApiServices services) =>
            Task.FromResult(context.Request.Headers["X-Test-Person"].ToString() is { Length: > 0 } person ? new Caller(person, roles) : null);
    }

    HttpClient Start(params string[] personRoles)
    {
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "starter", ["steerer", "approver"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [],
            new PolicyConfig([new ApprovalRule("project.requirements_session", ["approver"], 1, false, new Cap<bool>(false, false))], null, null, null, null),
            Path.Combine(folder, "organisation.yaml"));
        var app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!)
            { Authenticators = [new PersonAuthenticator(personRoles), new LocalTokenAuthenticator()] }, "http://127.0.0.1:0");
        app.StartAsync().GetAwaiter().GetResult();
        apps.Add(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "right");
        http.DefaultRequestHeaders.Add("X-Test-Person", "pat");
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in apps)
            await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    async Task OpenSessionAsync(bool open = true)
    {
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/project_delivery", "running", "starter", ["acme/api"], false, now, null)],
            [new WaitRecord("r1", "decision-1", open ? "decision" : "", "define", open ? "project.requirements_session" : "", "decision", ["submitted", "abandoned"], open, now)],
            Sessions: [new SessionRecord(Sid, "r1", "decision-1", "define", "", "acme/api", "ff/session/r1/decision-1", Commit, SessionStatuses.Prepared, null, null, now)]),
            TestContext.Current.CancellationToken);
    }

    async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient http, string path, string body)
    {
        using var response = await http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    async Task<IReadOnlyList<AnswerRecord>> AnswersAsync() =>
        (await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap();

    [Fact]
    public async Task A_person_without_a_role_that_may_answer_the_decision_may_read_the_session_but_not_act_on_it()
    {
        var http = Start("viewer");
        await OpenSessionAsync();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("runs/r1/sessions", TestContext.Current.CancellationToken)).StatusCode);
        foreach (var (path, body) in new[]
                 {
                     ($"sessions/{Sid}/submit", $$"""{ "revision": "{{Commit}}", "actor": "lead" }"""),
                     ($"sessions/{Sid}/checks", $$"""{ "step": "check_requirements", "revision": "{{Commit}}" }"""),
                     ($"sessions/{Sid}/abandon", "{}"),
                 })
        {
            var (status, text) = await PostAsync(http, path, body);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("pat may not act on session", text);
        }

        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task A_submission_or_check_names_a_full_commit_and_a_closed_session_takes_nothing()
    {
        var http = Start("approver");
        await OpenSessionAsync(open: false);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(http, $"sessions/{Sid}/submit", """{ "revision": "main" }""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(http, $"sessions/{Sid}/checks", """{ "step": "check_requirements", "revision": "HEAD" }""")).Status);
        var (status, text) = await PostAsync(http, $"sessions/{Sid}/submit", $$"""{ "revision": "{{Commit}}" }""");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("no longer open", text);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(http, "sessions/r1.decision-9/submit", $$"""{ "revision": "{{Commit}}" }""")).Status);
        Assert.Empty(await AnswersAsync());
    }
}
