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
/// The API's authentication and authorisation, which need no Temporal: a request without the local token is
/// refused, and an answer from an actor without the role the approval rule names is refused before anything is
/// recorded or sent (docs/05 section 3.2; F8: the actor and roles come from the credential, not the body).
/// </summary>
public sealed class ApiTests : IAsyncDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-api-" + Guid.NewGuid().ToString("N"));
    readonly SqliteRecordStore records;
    readonly Microsoft.AspNetCore.Builder.WebApplication app;
    readonly HttpClient http;

    public ApiTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "viewer_only", ["viewer"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [],
            new PolicyConfig([new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, false))], null, null, null, null),
            Path.Combine(folder, "organisation.yaml"));
        app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!), "http://127.0.0.1:0");
        app.StartAsync().GetAwaiter().GetResult();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? token, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_request_without_the_local_token_is_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(HttpMethod.Get, "runs", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(HttpMethod.Get, "runs", "wrong"));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Get, "runs", "right"));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(HttpMethod.Get, "runs/nope", "right"));
    }

    [Fact]
    public async Task An_answer_from_an_actor_without_the_rules_role_is_refused_and_nothing_is_recorded()
    {
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/ticket_delivery", "running", "x", [], false, now, null)],
            [new WaitRecord("r1", "decision-1", "decision", "approve_spec", "ticket.spec_approval", "decision", ["approved"], true, now)]), TestContext.Current.CancellationToken);

        // The body claims an approver; the token's actor is a viewer.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "runs/r1/answers", "right",
            """{ "wait_id": "decision-1", "outcome": "approved", "actor": "lead", "roles": ["approver"] }"""));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "runs/r1/abort", "right", "{}"));
        var answers = (await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, TestContext.Current.CancellationToken)).Unwrap();
        Assert.Empty(answers);
    }
}
