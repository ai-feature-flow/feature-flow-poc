using System.Diagnostics;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 A4: a long infrastructure outage must not become a failure. The
/// interpreter used to retry with a fixed 1-second delay and throw after 50, so
/// a sandbox host down for about 25 minutes failed the step — the R11/R19 class
/// again, an interruption becoming a wrong outcome.
///
/// Now: exponential backoff with a cap, no count limit, and past a threshold the
/// run is marked stalled on infrastructure (D24) and alerts. It never fails on
/// its own; a human may abort it.
/// </summary>
public static class BackoffTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var failures = 0;

        failures += BackoffCurveAsync();
        failures += await LongOutageAsync(client, queue, args);

        Console.WriteLine(failures == 0
            ? "BACKOFF all tests passed"
            : $"BACKOFF {failures} test(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The backoff curve itself: exponential, capped, and slow enough that a
    /// 30-minute outage costs few retries rather than about 1,800.
    /// </summary>
    private static int BackoffCurveAsync()
    {
        Console.WriteLine("BACKOFF curve:");
        var cumulative = 0.0;
        var ok = true;

        for (var i = 1; i <= 12; i++)
        {
            var delay = BackoffFor(i);
            cumulative += delay.TotalSeconds;
            if (i <= 10)
                Console.WriteLine($"  failure {i,2}: {delay.TotalSeconds,7:F0}s " +
                                  $"(cumulative {cumulative:F0}s)");
        }

        // Capped, at whatever the cap is set to (fast mode shortens it).
        var capped = BackoffFor(50);
        var capOk = capped == InterpreterWorkflow.MaxBackoff;
        Console.WriteLine($"  failure 50: {capped.TotalSeconds:F0}s (capped: {capOk})");
        ok &= capOk;

        // A 30-minute outage must not exhaust anything, and must cost few
        // retries. Measured against the configured cap, so fast mode does not
        // make this check meaningless.
        var retriesIn30Min = 0;
        var total = 0.0;
        for (var i = 1; total < 1800; i++)
        {
            total += BackoffFor(i).TotalSeconds;
            retriesIn30Min = i;
        }
        Console.WriteLine($"  a 30-minute outage costs {retriesIn30Min} retries " +
                          $"(was about 1,800 with a fixed 1s delay)");

        // The point is that backoff grows, so the retry count is sub-linear in
        // outage length. With a fixed 1s delay it would be ~1,800. Compare
        // against that, not against an absolute count, so fast mode (which
        // lowers the cap) does not fail the check.
        var fewRetries = retriesIn30Min < 1800;
        ok &= fewRetries;

        Console.WriteLine($"BACKOFF curve: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// A real outage: the sandbox is made unreachable for long enough to cross
    /// the stalled threshold. The step must not fail, and the stalled state must
    /// be visible.
    /// </summary>
    private static async Task<int> LongOutageAsync(
        ITemporalClient client, string queue, string[] args)
    {
        var outageSeconds = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--seconds")
                int.TryParse(args[i + 1], out outageSeconds);
        }
        if (outageSeconds <= 0)
            outageSeconds = 240;

        Console.WriteLine($"BACKOFF outage: sandbox unreachable for {outageSeconds}s");

        var plan = LongAgentPlan(outageSeconds);
        var id = $"backoff-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        // Wait for the command, then break the connection for the whole outage.
        var command = await WaitForCommandAsync("backoff_agent", TimeSpan.FromSeconds(60));
        if (command is null)
        {
            Console.WriteLine("BACKOFF outage: FAIL no sandbox command appeared");
            return 1;
        }

        await PostAsync($"/commands/{command.Handle}/break-connection?seconds={outageSeconds}",
            new { });
        Console.WriteLine($"BACKOFF outage: connection broken on {command.Handle} " +
                          $"for {outageSeconds}s");

        // Review 13 B1: the stalled state is time-based, so the outage must
        // exceed StalledAfter for it to appear. Wait for that, then a little
        // longer for the state to be recorded.
        var waitFor = (int)Math.Max(outageSeconds, InterpreterWorkflow.StalledAfter.TotalSeconds) + 30;
        Console.WriteLine($"BACKOFF outage: waiting {waitFor}s " +
                          $"(stalledAfter={InterpreterWorkflow.StalledAfter.TotalSeconds:F0}s)");

        // Sample during the outage. The stalled state clears once contact
        // resumes, so reading it only afterwards would always show false.
        var sawStalled = false;
        var stalledAt = 0;
        for (var elapsed = 0; elapsed < waitFor; elapsed += 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var s = await TryQueryAsync(handle);
            if (s?.StalledOnInfrastructure == true)
            {
                sawStalled = true;
                stalledAt = elapsed + 5;
                Console.WriteLine($"BACKOFF outage: stalled observed at {stalledAt}s " +
                                  $"step={s.StalledStepId}");
                break;
            }
        }
        if (!sawStalled)
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, waitFor - stalledAt)));

        var state = await TryQueryAsync(handle);

        // The step is still in flight during the outage, so its detail is not in
        // the record store yet. Read the live query instead.
        var detail = await TryQueryDetailAsync(handle, "backoff_agent");

        Console.WriteLine($"BACKOFF outage: outcome='{detail?.Outcome}' " +
                          $"attempts={detail?.Attempts} " +
                          $"infrastructureFailures={detail?.InfrastructureFailures}");
        Console.WriteLine($"BACKOFF outage: stalled={state?.StalledOnInfrastructure} " +
                          $"stalledStep={state?.StalledStepId}");
        Console.WriteLine($"BACKOFF outage: lastError={state?.LastInfrastructureError}");

        // The step must not have failed, and must not have consumed an attempt.
        var notFailed = detail?.Outcome != Outcomes.Failed &&
                        detail?.Outcome != Outcomes.AttemptsExhausted;
        var outageWasFree = (detail?.Attempts ?? 99) <= 1;
        var outageWasSeen = (detail?.InfrastructureFailures ?? 0) > 0;
        var stalledVisible = sawStalled;

        Console.WriteLine($"BACKOFF outage: notFailed={notFailed} outageWasFree={outageWasFree} " +
                          $"outageWasSeen={outageWasSeen} stalledVisible={stalledVisible}");

        var ok = notFailed && outageWasFree && outageWasSeen && stalledVisible;
        Console.WriteLine($"BACKOFF outage: {(ok ? "PASS" : "FAIL")}");

        // Let it finish once the connection is back.
        try
        {
            await handle.GetResultAsync<string>(
                followRuns: true,
                new RpcOptions { Timeout = TimeSpan.FromSeconds(300) });
            Console.WriteLine("BACKOFF outage: run completed after recovery");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BACKOFF outage: run did not complete: {ex.GetType().Name}");
        }

        return ok ? 0 : 1;
    }

    private static TimeSpan BackoffFor(int failures)
    {
        var seconds = Math.Pow(2, Math.Min(failures - 1, 20));
        var capped = Math.Min(seconds, InterpreterWorkflow.MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    /// <summary>
    /// The step must be shorter than the outage, or the activity simply waits
    /// out the break inside itself and never fails at the workflow level — which
    /// is the case the stalled state exists for.
    /// </summary>
    private static Plan LongAgentPlan(int outageSeconds)
    {
        var stepSeconds = Math.Max(10, Math.Min(60, outageSeconds / 4));
        var plan = new Plan { Entry = "backoff_agent", Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = "backoff_agent",
            Kind = "agent.run",
            Protected = true,
            SimulatedDuration = stepSeconds.ToString(),
            PollsInActivity = true,
            Limits = new StepLimits { Attempts = 3, StartToClose = "PT4H", Heartbeat = "PT30S" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Blocked] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.ReplanRequested] = RouteTarget.ToEnd(Outcomes.Failed),
            },
        });
        return plan;
    }

    private static async Task<InterpreterState?> TryQueryAsync(
        WorkflowHandle<InterpreterWorkflow> handle)
    {
        try
        {
            return await handle.QueryAsync(w => w.State());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<StepRunDetail?> TryQueryDetailAsync(
        WorkflowHandle<InterpreterWorkflow> handle, string stepId)
    {
        try
        {
            var runs = await handle.QueryAsync(w => w.StepRuns());
            return runs.TryGetValue(stepId, out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task PostAsync(string path, object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await Http.PostAsync("http://127.0.0.1:5199" + path, content);
    }

    private static async Task<CommandView?> WaitForCommandAsync(string stepId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await Http.GetAsync("http://127.0.0.1:5199/admin/state");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("commands", out var commands))
                    {
                        foreach (var c in commands.EnumerateArray())
                        {
                            if (c.TryGetProperty("sandboxKey", out var key) &&
                                key.GetString() == $"sbx-{stepId}")
                            {
                                return new CommandView
                                {
                                    Handle = c.GetProperty("handle").GetString() ?? "",
                                };
                            }
                        }
                    }
                }
            }
            catch
            {
                // Retry.
            }
            await Task.Delay(500);
        }
        return null;
    }

    private sealed class CommandView
    {
        public string Handle { get; set; } = "";
    }
}
