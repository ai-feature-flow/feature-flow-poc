using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace FeatureFlow.Spike.Worker;

/// <summary>
/// Activities backed by the out-of-process fake sandbox. The sandbox is a
/// separate OS process, so it survives a worker kill — which is the whole point
/// of exit criterion 1.
/// </summary>
public sealed class SandboxActivities : ISpikeActivities
{
    private readonly HttpClient http;
    private readonly string baseUrl;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SandboxActivities(string baseUrl)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }
    [Activity]
    public async Task<AgentRunResult> RunAgentAsync(AgentRunRequest request)
    {
        // Idempotency key is the sandbox re-attachment mechanism (docs/01 3.6).
        // A retry after a worker crash hits the same key and re-attaches.
        var start = await PostAsync<CommandView>("/commands", new
        {
            stepId = request.StepId,
            idempotencyKey = request.IdempotencyKey,
            sessionRef = request.SessionRef,
            durationSeconds = request.DurationSeconds,
            produceCommits = request.ProduceCommits,
            commitEverySeconds = request.CommitEverySeconds,
        });

        // docs/05 1.2.1: poll inside ONE activity, heartbeating. One activity per
        // step, not one per poll. This is exit criterion 8 (R20).
        var infrastructureRetries = 0;
        while (true)
        {
            CommandView view;
            try
            {
                view = await GetAsync<CommandView>($"/commands/{start.Handle}");
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                // K3: the worker-to-sandbox connection broke. The command keeps
                // running in the sandbox; this is infrastructure, so it retries
                // free and does not consume an attempt (docs/01 3.7).
                infrastructureRetries++;
                Console.WriteLine($"ACTIVITY {request.StepId} attempt {request.AttemptNumber}: " +
                                  $"sandbox unreachable, retry {infrastructureRetries}");
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                continue;
            }

            // The hook endpoint going away is infrastructure, not a denied tool
            // call (R19).
            var hook = await TryGetAsync<HookView>($"/hook/{request.StepId}");
            if (hook is null)
            {
                ActivityExecutionContext.Current.Heartbeat(new object?[] { Progress(view) });
                throw new ApplicationException("hook endpoint unreachable");
            }

            ActivityExecutionContext.Current.Heartbeat(new object?[] { Progress(view) });

            if (view.Status == "completed")
            {
                return new AgentRunResult(
                    Outcomes.Completed,
                    view.LastCommit,
                    view.SessionRef,
                    view.SessionTurns,
                    view.Handle,
                    view.SandboxKey,
                    null,
                    null,
                    infrastructureRetries);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private static AgentProgress Progress(CommandView view) => new(
        view.LastCommit, view.SessionTurns, view.SessionRef, view.Handle, view.SandboxKey,
        view.ElapsedSeconds);

    /// <summary>
    /// docs/05 1.3: unreachable is Infrastructure, so it retries free. A 503
    /// from the sandbox is the K3 injection; anything else is treated as work.
    /// </summary>
    private static bool IsUnreachable(Exception ex) =>
        ex.Message.Contains("503", StringComparison.Ordinal) ||
        ex.Message.Contains("unreachable", StringComparison.Ordinal);
    [Activity]
    public async Task<AgentRunResult> RunGenericStepAsync(GenericStepRequest request)
    {
        await Task.Delay(TimeSpan.FromSeconds(request.DurationSeconds));
        return new AgentRunResult(Outcomes.Completed);
    }
    [Activity]
    public Task<PlanPatch?> ProposePatchAsync(PatchRequest request)
    {
        PlanPatch? patch = request.Intent switch
        {
            "remove_protected" => PlanGenerator.RemoveProtected(request.BaseRevision),
            "bypass_protected" => PlanGenerator.BypassProtected(request.BaseRevision),
            "insert_after" => PlanGenerator.InsertAfterRunTests(request.BaseRevision),
            _ => null,
        };
        return Task.FromResult(patch);
    }
    [Activity]
    public async Task<string> WaitForDecisionAsync(DecisionRequest request)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        return Outcomes.Approved;
    }
    [Activity]
    public Task RecordPatchResultAsync(PatchRecord record)
    {
        Console.WriteLine($"PATCH {record.Result} base={record.BaseRevision} reason={record.Reason}");
        return Task.CompletedTask;
    }
    [Activity]
    public Task<long> ReadHistorySizeAsync(string runId) => Task.FromResult(0L);

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body, Json),
            System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(baseUrl + path, content);
        await EnsureSuccessAsync(response, path);
        return await ReadAsync<T>(response, path);
    }

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await http.GetAsync(baseUrl + path);
        await EnsureSuccessAsync(response, path);
        return await ReadAsync<T>(response, path);
    }

    private async Task<T?> TryGetAsync<T>(string path) where T : class
    {
        var response = await http.GetAsync(baseUrl + path);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return null;
        return await ReadAsync<T>(response, path);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync();
        // docs/05 1.3: unreachable is Infrastructure, so it retries free.
        throw new ApplicationException(
            $"sandbox {path} -> {(int)response.StatusCode} {body}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string path)
    {
        var body = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize<T>(body, Json);
        if (value is null)
            throw new ApplicationException($"sandbox {path} returned no body");
        return value;
    }

    private sealed class CommandView
    {
        public string Handle { get; set; } = "";
        public string Status { get; set; } = "";
        public string? SessionRef { get; set; }
        public string? LastCommit { get; set; }
        public int SessionTurns { get; set; }
        public double ElapsedSeconds { get; set; }
        public string? SandboxKey { get; set; }
    }

    private sealed class HookView
    {
        public bool Ok { get; set; }
    }
}
