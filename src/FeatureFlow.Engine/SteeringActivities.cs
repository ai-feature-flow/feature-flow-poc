using System.Text;
using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Library;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine;

/// <summary>
/// A mid-run library override (docs/08 section 5.2): the run's lock, the library and file to replace, and the new content,
/// or, for a run inheriting its parent's override, the content's hash, already stored.
/// </summary>
public sealed record LibraryOverrideCall(string LockRef, string Library, string Path, string? Content, string? ContentHash);

/// <summary>The run's new lock, the file's old and new content hashes, and the libraries as the run now resolves them.</summary>
public sealed record LibraryOverrideResult(string LockRef, string OldHash, string NewHash, IReadOnlyList<LibrarySnapshot> Libraries);

/// <summary>An aborted run's pull requests, as <c>owner/repo#n</c>, and the child runs it started, whose own are closed too (docs/05 section 5).</summary>
public sealed record ClosePullRequestsCall(string RunId, IReadOnlyList<string> PullRequests, string Reason, bool DryRun, IReadOnlyList<string>? Children = null);

/// <summary>The engine's activities for a person's steering (docs/05 section 5): library overrides, and closing an aborted run's pull requests.</summary>
public sealed partial class RunActivities
{
    /// <summary>
    /// Stores the new content by its hash and writes the run's new lock with the one file replaced: the lock entry of docs/08
    /// section 5.1, so every worker resolves the run's content from the lock as before. Idempotent: the same content gives
    /// the same hashes and the same lock.
    /// </summary>
    [Activity]
    public async Task<LibraryOverrideResult> OverrideLibraryAsync(LibraryOverrideCall call)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var current = JsonSerializer.Deserialize<LibraryLock>(await ReadTextAsync(call.LockRef), Json)
                      ?? throw new ApplicationFailureException("the lock does not read", nonRetryable: true);
        var library = current.Libraries.FirstOrDefault(l => l.Name == call.Library)
                      ?? throw new ApplicationFailureException($"the lock has no library {call.Library}", nonRetryable: true);
        var oldHash = library.Files.GetValueOrDefault(call.Path)
                      ?? throw new ApplicationFailureException($"the lock has no file {call.Library}/{call.Path}", nonRetryable: true);
        string newHash;
        if (call.Content is { } content)
        {
            newHash = LibraryFiles.Hash(content);
            var stored = await services.ArtifactStore.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), ct);
            if (!stored.Ok)
                throw new ApplicationFailureException($"artifact store: {stored.Error!.Message}", nonRetryable: stored.Error.Class == ProviderErrorClass.Work);
            if (stored.Value!.Id != newHash)
                throw new ApplicationFailureException($"the artifact store named the content {stored.Value.Id}, not its hash {newHash}", nonRetryable: true);
        }
        else
            newHash = call.ContentHash ?? throw new ApplicationFailureException("a library override needs its content or the content's hash", nonRetryable: true);

        var files = new SortedDictionary<string, string>(library.Files.ToDictionary(), StringComparer.Ordinal) { [call.Path] = newHash };
        var next = new LibraryLock(current.Libraries.Select(l => l.Name == call.Library ? l with { Files = files } : l).ToList());
        var lockText = JsonSerializer.Serialize(next, Json);
        var written = await services.ArtifactStore.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct);
        if (!written.Ok)
            throw new ApplicationFailureException($"artifact store: {written.Error!.Message}", nonRetryable: written.Error.Class == ProviderErrorClass.Work);
        return new LibraryOverrideResult(written.Value!.Id, oldHash, newHash, await LoadLibrariesAsync(written.Value.Id));
    }

    /// <summary>
    /// Closes each of an aborted run's pull requests that is still open, with a comment saying why, and leaves merged and
    /// closed ones as they are. Returns a line per pull request for the run's timeline. A code host that cannot be reached is
    /// retried; one that refuses is noted, and the abort stands.
    /// </summary>
    [Activity]
    public async Task<IReadOnlyList<string>> ClosePullRequestsAsync(ClosePullRequestsCall call)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        if (services.Providers.ForRun("code_host", call.DryRun) is not ICodeHostProvider host)
            return [$"no code host is configured: {string.Join(", ", call.PullRequests)} left as they are"];
        var events = new List<string>();
        foreach (var text in call.PullRequests.Concat(await DescendantPullRequestsAsync(call.Children ?? [], ct)).Distinct())
        {
            if (ActionRun.ParsePullRequest(text) is not { } pr)
            {
                events.Add($"{text}: not a pull request reference; left");
                continue;
            }

            var state = await host.GetPullRequestAsync(pr, ct);
            if (!state.Ok)
            {
                Infrastructure(state.Error!);
                events.Add($"{text}: could not be read ({state.Error!.Message}); left");
                continue;
            }

            if (state.Value!.State != PullRequestStates.Open)
            {
                events.Add($"{text}: {state.Value.State}; left");
                continue;
            }

            var closed = await host.ClosePullRequestAsync(pr, $"Closed by Feature Flow: run {call.RunId} was aborted, and the person asked for its pull requests to be closed. {call.Reason}", ct);
            if (!closed.Ok)
            {
                Infrastructure(closed.Error!);
                events.Add($"{text}: the code host refused to close it ({closed.Error!.Message})");
                continue;
            }

            events.Add($"{text}: closed");
        }

        return events;
    }

    /// <summary>The pull requests of the run tree below an aborted run: each child answers the pull requests query, running or ended.</summary>
    async Task<List<string>> DescendantPullRequestsAsync(IReadOnlyList<string> children, CancellationToken ct)
    {
        var found = new List<string>();
        var queue = new Queue<string>(children);
        while (client is not null && queue.TryDequeue(out var id))
        {
            RunPullRequests answer;
            try
            {
                answer = await client.GetWorkflowHandle(id).QueryAsync<RunPullRequests>(RunWorkflow.PullRequestsQuery, [], new() { Rpc = new() { CancellationToken = ct } });
            }
            catch (Exception e) when (e is Temporalio.Exceptions.RpcException { Code: Temporalio.Exceptions.RpcException.StatusCode.NotFound } or Temporalio.Exceptions.WorkflowQueryFailedException)
            {
                // A run.child execution that never started its child, or a child from before the query existed.
                continue;
            }

            found.AddRange(answer.PullRequests);
            foreach (var child in answer.Children)
                queue.Enqueue(child);
        }

        return found;
    }

    static void Infrastructure(ProviderError error)
    {
        if (error.Class == ProviderErrorClass.Infrastructure)
            throw new ApplicationFailureException(error.Message, errorType: "infrastructure");
    }
}

public sealed partial class RunActivities
{
    /// <summary>
    /// The organisation's <c>planning.require_review</c> for a template (docs/05 section 3.1): the first rule whose pattern
    /// matches the template's reference or its name, <c>*</c> matching any run of characters. Null when none matches.
    /// </summary>
    public static bool? RequiresReview(Model.PolicyConfig policy, string template)
    {
        var name = Model.LibraryReference.TryParse(template)?.Name;
        return policy.RequireReview.FirstOrDefault(r => Glob(r.Match, template) || (name is not null && Glob(r.Match, name)))?.Value.Value;
    }

    static bool Glob(string pattern, string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$");
}
