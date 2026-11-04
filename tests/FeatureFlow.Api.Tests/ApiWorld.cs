using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Converters;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// A Temporal client that delivers nothing: every update the API sends is kept, with its arguments, and answered with
/// <see cref="Reply"/>. Its connection is lazy and never used, so no Temporal server is needed.
/// </summary>
public sealed class FakeTemporal : IClientInterceptor
{
    public FakeTemporal() =>
        Client = new TemporalClient(TemporalConnection.CreateLazy(new TemporalConnectionOptions("127.0.0.1:1")), new TemporalClientOptions { Interceptors = [this] });

    public ITemporalClient Client { get; }

    public List<(string WorkflowId, string Update, object? Argument)> Updates { get; } = [];

    public string Reply { get; set; } = "recorded";

    public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor nextInterceptor) => new Outbound(this, nextInterceptor);

    static readonly PropertyInfo KnownOutcome = typeof(WorkflowUpdateHandle).GetProperty("KnownOutcome", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    sealed class Outbound(FakeTemporal fake, ClientOutboundInterceptor next) : ClientOutboundInterceptor(next)
    {
        public override Task<WorkflowUpdateHandle<TResult>> StartWorkflowUpdateAsync<TResult>(StartWorkflowUpdateInput input)
        {
            lock (fake.Updates)
                fake.Updates.Add((input.Id, input.Update, input.Args.SingleOrDefault()));
            var handle = new WorkflowUpdateHandle<TResult>(fake.Client, input.Options.Id ?? "update", input.Id, input.RunId);
            var payload = DataConverter.Default.PayloadConverter.ToPayload(new AnswerReply(fake.Reply));
            KnownOutcome.SetValue(handle, new Temporalio.Api.Update.V1.Outcome { Success = new Payloads { Payloads_ = { payload } } });
            return Task.FromResult(handle);
        }
    }
}

/// <summary>
/// An API over SQLite records and <see cref="FakeTemporal"/>, whose callers are named by test headers: <c>X-Person</c>,
/// <c>X-Roles</c> (comma separated) or <c>X-Groups</c>, and <c>X-Channel</c>. A body may claim anything; only the headers
/// stand for the authenticated identity.
/// </summary>
public sealed class ApiWorld : IAsyncDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-apiw-" + Guid.NewGuid().ToString("N"));
    readonly Microsoft.AspNetCore.Builder.WebApplication app;

    public ApiWorld(OrganisationConfig? organisation = null, IReadOnlyList<IAuthenticator>? authenticators = null, ApiAuthConfig? auth = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        Records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = Records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            auth ?? new ApiAuthConfig("local_token", "secrets/api_token", "local", ["viewer", "approver", "steerer", "admin"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        Organisation = (organisation ?? new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), "")) with { Location = Path.Combine(folder, "organisation.yaml") };
        app = ApiApp.Build(new ApiServices(providers, installation, Organisation, Temporal.Client)
        {
            Authenticators = authenticators ?? [new HeaderAuthenticator()],
            PeopleRoles = new OrganisationRoles(Organisation),
        }, "http://127.0.0.1:0");
        app.StartAsync().GetAwaiter().GetResult();
        Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public SqliteRecordStore Records { get; }

    public FakeTemporal Temporal { get; } = new();

    public OrganisationConfig Organisation { get; }

    public Uri Address { get; }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    sealed class HeaderAuthenticator : IAuthenticator
    {
        public Task<Caller?> AuthenticateAsync(HttpContext context, ApiServices services)
        {
            var person = context.Request.Headers["X-Person"].ToString();
            if (person.Length == 0)
                return Task.FromResult<Caller?>(null);
            static string[] List(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var groups = context.Request.Headers["X-Groups"].ToString();
            var channel = context.Request.Headers["X-Channel"].ToString();
            return Task.FromResult<Caller?>(new Caller(person, List(context.Request.Headers["X-Roles"].ToString()), groups.Length == 0 ? null : List(groups),
                channel.Length == 0 ? CallerChannels.Api : channel));
        }
    }

    /// <summary>A client acting as <paramref name="person"/> with <paramref name="roles"/>, or with identity-provider <paramref name="groups"/>.</summary>
    public HttpClient As(string person, string roles = "", string? groups = null, string? channel = null)
    {
        var http = new HttpClient { BaseAddress = Address };
        http.DefaultRequestHeaders.Add("X-Person", person);
        http.DefaultRequestHeaders.Add("X-Roles", roles);
        if (groups is not null)
            http.DefaultRequestHeaders.Add("X-Groups", groups);
        if (channel is not null)
            http.DefaultRequestHeaders.Add("X-Channel", channel);
        return http;
    }

    public static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient http, string path, string body)
    {
        using var response = await http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public async Task RunAsync(string runId, params string[] repositories) =>
        await Records.AppendAsync(new RecordBatch([new RunRecord(runId, null, "std:templates/ticket_delivery", "running", "x", repositories, false, DateTimeOffset.UtcNow, null)]),
            TestContext.Current.CancellationToken);

    public async Task WaitAsync(string runId, string waitId, string kind, string type) =>
        await Records.AppendAsync(new RecordBatch(Waits: [new WaitRecord(runId, waitId, kind, "implement", type, "reason", [], true, DateTimeOffset.UtcNow)]),
            TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string query, string runId) =>
        (await Records.QueryAsync<T>(query, new Dictionary<string, string> { ["run_id"] = runId }, TestContext.Current.CancellationToken)).Unwrap();

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}
