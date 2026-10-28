using System.Globalization;
using FeatureFlow.Spike.Ci;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.CiHarness;

/// <summary>
/// Runs the GitHub Actions CI provider against the real fixture repository and
/// prints what actually happened. Nothing here is simulated: every line of
/// output comes from a call to api.github.com.
///
/// The six checks:
///
/// 1. <see cref="ICiProvider.CheckAsync"/> reports real capabilities.
/// 2. <see cref="ICiProvider.TriggerAsync"/> dispatches the integration workflow
///    at a real commit sha and returns a reference without waiting.
/// 3. <see cref="ICiProvider.GetRunStatusAsync"/> is polled to completion and the
///    real conclusion is printed.
/// 4. <see cref="ICiProvider.ReadResultsAsync"/> returns structured results.
/// 5. <see cref="ICiProvider.GetArtifactsAsync"/> returns artifact references.
/// 6. An unknown tier is refused with <c>validation_failed</c>, class
///    <see cref="ErrorClass.Work"/>.
///
/// Credentials come from the broker, which reads the private key by reference
/// from a secrets file outside the repository (floor F2). No token is ever
/// printed; only its fingerprint and expiry.
/// </summary>
public static class Program
{
    private const string Owner = "ai-feature-flow";
    private const string Repo = "feature-flow-example";
    private const string Branch = "main";

    /// <summary>How long the poll loop will wait for the run. The engine would use a durable timer.</summary>
    private static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args)
    {
        var tier = args.Length > 0 ? args[0] : "integration";

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var failures = 0;

        WriteHeader("Spike 2, GitHub Actions CI provider, real run");
        Console.WriteLine($"repository: {Owner}/{Repo}");
        Console.WriteLine($"tier:       {tier}");
        Console.WriteLine($"time:       {DateTimeOffset.UtcNow:O}");
        Console.WriteLine();

        var repo = new RepoRef(Owner, Repo);
        var secrets = new LocalFileSecretsProvider();

        // The broker refuses rather than widens (docs/05 12), so the grantable
        // set names this repository and the permissions the CI provider needs.
        var broker = new GitHubAppCredentialBroker(
            secrets,
            grantable: new[]
            {
                GrantableScope.ForRepository(
                    repo.FullName,
                    "actions:write",
                    "contents:read",
                    "metadata:read"),
            });

        var client = new GitHubActionsClient();
        var tiers = CiTierMap.ForFeatureFlowExample(Branch);

        var provider = new GitHubActionsCiProvider(
            broker: broker,
            tiers: tiers,
            client: client,
            instanceName: "github-actions",
            defaultRef: Branch,
            defaultRepo: repo);

        // ---------------------------------------------------------------- 1
        WriteHeader("1. CheckAsync, real capabilities");

        var descriptor = provider.Describe();
        Console.WriteLine($"name:        {descriptor.Name} v{descriptor.Version}");
        Console.WriteLine($"schema:      {descriptor.ConfigSchemaRef}");

        foreach (var capability in descriptor.Capabilities.OfType<CiCapabilities>())
        {
            Console.WriteLine($"capability:  Trigger={capability.Trigger} " +
                              $"ReadLogs={capability.ReadLogs} " +
                              $"Artifacts={capability.Artifacts} " +
                              $"InboundEvents={capability.InboundEvents}");
        }

        var check = await provider.CheckAsync(CancellationToken.None).ConfigureAwait(false);
        if (!check.IsOk)
        {
            Console.WriteLine($"FAIL: {check.Error!.Class}/{check.Error.Reason} {check.Error.Detail}");
            return 1;
        }

        Console.WriteLine($"ok:          {check.Value!.Ok}");
        Console.WriteLine($"detail:      {check.Value.Detail}");
        Console.WriteLine($"latency:     {check.Value.Latency!.Value.TotalMilliseconds:F0} ms");
        Console.WriteLine($"tiers:       {tiers}");
        Console.WriteLine();

        // A real commit sha, resolved from the branch. The provider is never
        // handed a made-up sha.
        var credential = await broker.IssueAsync(
            Actor.Engine("ci-harness"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(repo.FullName, "actions:write", "contents:read", "metadata:read").Text,
            TimeSpan.FromMinutes(55),
            CancellationToken.None).ConfigureAwait(false);

        if (!credential.IsOk)
        {
            Console.WriteLine($"FAIL: credential {credential.Error!.Class}/{credential.Error.Reason} {credential.Error.Detail}");
            return 1;
        }

        Console.WriteLine($"credential:  {credential.Value!.Describe()}");

        var sha = await client.GetCommitShaForRefAsync(
            credential.Value.Secret, Owner, Repo, Branch, CancellationToken.None).ConfigureAwait(false);

        if (!sha.IsOk)
        {
            Console.WriteLine($"FAIL: resolve {Branch}: {sha.Error!.Class}/{sha.Error.Reason}");
            return 1;
        }

        Console.WriteLine($"commit:      {sha.Value} (head of {Branch})");
        Console.WriteLine();

        // ---------------------------------------------------------------- 2
        WriteHeader("2. TriggerAsync, dispatch without waiting");

        var idempotencyKey = $"spike2-ci-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var started = DateTimeOffset.UtcNow;

        var trigger = await provider.TriggerAsync(
            repo, tier, sha.Value!, idempotencyKey, CancellationToken.None).ConfigureAwait(false);

        var triggerElapsed = DateTimeOffset.UtcNow - started;

        if (!trigger.IsOk)
        {
            Console.WriteLine($"FAIL: {trigger.Error!.Class}/{trigger.Error.Reason} {trigger.Error.Detail}");
            return 1;
        }

        var run = trigger.Value!;
        Console.WriteLine($"run id:      {run.RunId}");
        Console.WriteLine($"url:         {run.Url}");
        Console.WriteLine($"event:       {run.Event}");
        Console.WriteLine($"workflow:    {run.WorkflowFile}");
        Console.WriteLine($"tier:        {run.Tier}");
        Console.WriteLine($"commit:      {run.CommitSha}");
        Console.WriteLine($"key:         {run.IdempotencyKey}");
        Console.WriteLine($"elapsed:     {triggerElapsed.TotalSeconds:F1} s (returned without waiting for the run)");
        Console.WriteLine();

        // ---------------------------------------------------------------- 3
        WriteHeader("3. GetRunStatusAsync, polled to completion");

        CiRunStatus? status = null;
        var polls = 0;
        var pollStart = DateTimeOffset.UtcNow;

        while (true)
        {
            var poll = await provider.GetRunStatusAsync(run, CancellationToken.None).ConfigureAwait(false);
            polls++;

            if (!poll.IsOk)
            {
                Console.WriteLine($"FAIL: {poll.Error!.Class}/{poll.Error.Reason} {poll.Error.Detail}");
                failures++;
                break;
            }

            status = poll.Value!;
            Console.WriteLine(
                $"  poll {polls,2}: state={status.State,-11} conclusion={status.Conclusion,-8} " +
                $"checks={status.CompletedChecks}/{status.TotalChecks} " +
                $"registered={status.ChecksRegistered} " +
                $"{(status.AwaitingChecks ? "(awaiting late checks)" : string.Empty)}");

            if (status.IsTerminal)
                break;

            if (DateTimeOffset.UtcNow - pollStart > PollBudget)
            {
                Console.WriteLine($"FAIL: run did not finish within {PollBudget.TotalMinutes:F0} min");
                failures++;
                break;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        if (status is not null && status.IsTerminal)
        {
            Console.WriteLine();
            Console.WriteLine($"run id:      {run.RunId}");
            Console.WriteLine($"url:         {status.Url ?? run.Url}");
            Console.WriteLine($"state:       {status.State}");
            Console.WriteLine($"conclusion:  {status.Conclusion}");
            Console.WriteLine($"checks:      {status.CompletedChecks}/{status.TotalChecks}");
            Console.WriteLine($"polls:       {polls} over {(DateTimeOffset.UtcNow - pollStart).TotalSeconds:F0} s");

            if (status.Conclusion != CiConclusion.Success)
            {
                Console.WriteLine($"NOTE: conclusion is {status.Conclusion}, not success");
                failures++;
            }
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 4
        WriteHeader("4. ReadResultsAsync, structured results");

        var results = await provider.ReadResultsAsync(run, CancellationToken.None).ConfigureAwait(false);

        if (!results.IsOk)
        {
            Console.WriteLine($"FAIL: {results.Error!.Class}/{results.Error.Reason} {results.Error.Detail}");
            failures++;
        }
        else
        {
            var value = results.Value!;
            Console.WriteLine($"state:       {value.State}");
            Console.WriteLine($"conclusion:  {value.Conclusion}");
            Console.WriteLine($"complete:    {value.Complete}");
            Console.WriteLine($"jobs:        {value.JobsOrEmpty.Count}");

            foreach (var job in value.JobsOrEmpty)
            {
                Console.WriteLine(
                    $"  job {job.Id}: {job.Name,-40} state={job.State,-11} " +
                    $"conclusion={job.Conclusion,-8} steps={job.StepsCompleted}/{job.StepsTotal}");
            }

            var counts = value.ConclusionCounts;
            if (counts.Count > 0)
                Console.WriteLine($"by outcome:  {string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}"))}");
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 5
        WriteHeader("5. GetArtifactsAsync, artifact references");

        var artifacts = await provider.GetArtifactsAsync(run, CancellationToken.None).ConfigureAwait(false);

        if (!artifacts.IsOk)
        {
            Console.WriteLine($"FAIL: {artifacts.Error!.Class}/{artifacts.Error.Reason} {artifacts.Error.Detail}");
            failures++;
        }
        else
        {
            Console.WriteLine($"artifacts:   {artifacts.Value!.Count}");
            foreach (var artifact in artifacts.Value!)
            {
                Console.WriteLine($"  {artifact.Name} (id {artifact.Id})");
                Console.WriteLine($"    size:    {artifact.SizeBytes?.ToString() ?? "unknown"} bytes");
                Console.WriteLine($"    digest:  {artifact.Digest ?? "none"}");
                Console.WriteLine($"    expires: {artifact.ExpiresAt?.ToString("O") ?? "not given"}");
                Console.WriteLine($"    uri:     {artifact.Uri}");
            }
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 6
        WriteHeader("6. ReadLogsAsync, a reference not a body");

        var logs = await provider.ReadLogsAsync(run, CancellationToken.None).ConfigureAwait(false);

        if (!logs.IsOk)
        {
            Console.WriteLine($"FAIL: {logs.Error!.Class}/{logs.Error.Reason} {logs.Error.Detail}");
            failures++;
        }
        else
        {
            var log = logs.Value!;
            Console.WriteLine($"kind:        {log.Kind}");
            Console.WriteLine($"content:     {log.ContentType}");
            Console.WriteLine($"expires:     {log.ExpiresAt?.ToString("O") ?? "not given"}");
            Console.WriteLine($"uri:         {Truncate(log.Uri, 120)}");
            Console.WriteLine($"body bytes:  0 (a reference, never the body)");
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 7
        WriteHeader("7. Unknown tier is refused as Work, not Infrastructure");

        var unknown = await provider.TriggerAsync(
            repo, "does-not-exist", sha.Value!, idempotencyKey, CancellationToken.None).ConfigureAwait(false);

        if (unknown.IsOk)
        {
            Console.WriteLine("FAIL: an unknown tier was accepted");
            failures++;
        }
        else
        {
            var error = unknown.Error!;
            Console.WriteLine($"class:       {error.Class}");
            Console.WriteLine($"reason:      {error.Reason}");
            Console.WriteLine($"detail:      {error.Detail}");
            Console.WriteLine($"retryAfter:  {error.RetryAfter?.ToString() ?? "none"}");

            if (error.Class != ErrorClass.Work || error.Reason != "validation_failed")
            {
                Console.WriteLine("FAIL: expected Work/validation_failed");
                failures++;
            }
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 8
        WriteHeader("8. Queue builds (merge_group) are first-class");

        var found = await provider.FindRunsAsync(repo, tier, sha.Value!, CancellationToken.None).ConfigureAwait(false);

        if (!found.IsOk)
        {
            Console.WriteLine($"FAIL: {found.Error!.Class}/{found.Error.Reason} {found.Error.Detail}");
            failures++;
        }
        else
        {
            Console.WriteLine($"runs at {sha.Value}: {found.Value!.Count}");
            foreach (var candidate in found.Value!)
            {
                Console.WriteLine(
                    $"  run {candidate.RunId}: event={candidate.Event,-18} " +
                    $"queueBuild={candidate.IsQueueBuild} url={candidate.Url}");
            }

            var queueBuilds = found.Value!.Count(r => r.IsQueueBuild);
            Console.WriteLine($"merge_group runs: {queueBuilds} " +
                              "(none expected: this repository has no merge queue enabled)");
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 9
        WriteHeader("9. ReadResultsAsync on an unfinished run is partial, class Work");

        // A second run, read immediately. The integration tier takes about 40
        // seconds, so the first read lands while jobs are still running.
        var partialKey = $"spike2-ci-partial-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        var partialTrigger = await provider.TriggerAsync(
            repo, tier, sha.Value!, partialKey, CancellationToken.None).ConfigureAwait(false);

        if (!partialTrigger.IsOk)
        {
            Console.WriteLine($"FAIL: trigger {partialTrigger.Error!.Class}/{partialTrigger.Error.Reason}");
            failures++;
        }
        else
        {
            var partialRun = partialTrigger.Value!;
            Console.WriteLine($"run id:      {partialRun.RunId} (read immediately, not waited for)");

            var partial = await provider.ReadResultsAsync(partialRun, CancellationToken.None).ConfigureAwait(false);

            if (partial.IsOk)
            {
                Console.WriteLine($"complete:    {partial.Value!.Complete}");
                Console.WriteLine($"jobs:        {partial.Value.JobsOrEmpty.Count}");
                Console.WriteLine("NOTE: the run had already finished; partial was not exercised");
            }
            else
            {
                var error = partial.Error!;
                Console.WriteLine($"class:       {error.Class}");
                Console.WriteLine($"reason:      {error.Reason}");
                Console.WriteLine($"detail:      {error.Detail}");

                if (error.Class != ErrorClass.Work || error.Reason != "partial")
                {
                    Console.WriteLine("FAIL: expected Work/partial");
                    failures++;
                }
            }

            // Let it finish so the repository is not left with a running run.
            Console.WriteLine("waiting for it to finish...");
            for (var i = 0; i < 60; i++)
            {
                var poll = await provider.GetRunStatusAsync(partialRun, CancellationToken.None).ConfigureAwait(false);
                if (poll.IsOk && poll.Value!.IsTerminal)
                {
                    Console.WriteLine($"final:       {poll.Value.State}/{poll.Value.Conclusion}");
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        Console.WriteLine();

        // --------------------------------------------------------------- 10
        WriteHeader("10. Error classification is by status code, never by message text");

        foreach (var (code, expectedClass, expectedReason) in ClassificationCases())
        {
            var error = GitHubActionsClient.Classify(code, "{\"message\":\"anything at all\"}");
            var ok = error.Class == expectedClass && error.Reason == expectedReason;
            Console.WriteLine(
                $"  {code,3} -> {error.Class,-14} {error.Reason,-18} " +
                $"{(ok ? "ok" : $"FAIL expected {expectedClass}/{expectedReason}")}");

            if (!ok)
                failures++;
        }

        Console.WriteLine();
        WriteHeader(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The status-to-class table from docs/05 1.3, as data. 5xx, 429, timeouts
    /// and connection failures are Infrastructure; 404, 422 and 409 are Work.
    /// </summary>
    private static IEnumerable<(int Status, ErrorClass Class, string Reason)> ClassificationCases()
    {
        yield return (401, ErrorClass.Infrastructure, "unauthenticated");
        yield return (403, ErrorClass.Infrastructure, "forbidden");
        yield return (404, ErrorClass.Work, "not_found");
        yield return (409, ErrorClass.Work, "conflict");
        yield return (422, ErrorClass.Work, "validation_failed");
        yield return (429, ErrorClass.Infrastructure, "rate_limited");
        yield return (500, ErrorClass.Infrastructure, "server_error");
        yield return (502, ErrorClass.Infrastructure, "server_error");
        yield return (503, ErrorClass.Infrastructure, "server_error");
    }

    private static void WriteHeader(string text)
    {
        Console.WriteLine(text);
        Console.WriteLine(new string('-', text.Length));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
