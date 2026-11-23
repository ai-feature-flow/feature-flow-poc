using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Actions;

/// <summary>
/// The run's pull requests as its steps read them (A5), and the title a pull request is opened with (E1).
/// </summary>
static class PullRequests
{
    /// <summary>
    /// Records a read of a pull request, with the checks read alongside it if any, as a <see cref="PullRequestRecord"/>
    /// under the reading step: only when something changed since the run's last record of it (state, files, checks or the
    /// reading step), so a polling step writes nothing while the pull request stands still. A read without checks keeps the
    /// recorded checks of the same head. The files are listed again only when the head or base moved, or were not read; a
    /// files list that does not read is left out, never failing the step.
    /// </summary>
    public static async Task RecordAsync(ActionRun run, PullRequestState state, CheckStatus? checks = null)
    {
        var records = run.Providers.Required<IRecordStoreProvider>("record_store");
        var pr = state.PullRequest;
        var runId = run.Call.Step.RunId;
        var stored = ActionRun.Must(await records.QueryAsync<PullRequestRecord>(Queries.PullRequests, new Dictionary<string, string> { ["run_id"] = runId }, run.Ct),
                "read the run's pull requests")
            .LastOrDefault(p => p.Repository == pr.Repo.FullName && p.Number == pr.Number);
        var sameHead = stored is not null && stored.State.HeadRevision == state.HeadRevision && stored.State.Base == state.Base;
        checks ??= stored?.Checks is { } kept && kept.Revision == state.HeadRevision ? kept : null;
        var files = sameHead && stored!.Files is { } known ? known : await FilesAsync(run, pr);
        var record = new PullRequestRecord(runId, pr.Repo.FullName, pr.Number, run.Call.Step.StepId, state, files, checks, DateTimeOffset.UtcNow);
        if (stored is not null && Same(stored, record))
            return;
        ActionRun.Must(await records.AppendAsync(new RecordBatch(PullRequests: [record]), run.Ct), "record the pull request");
    }

    static async Task<IReadOnlyList<PullRequestFile>?> FilesAsync(ActionRun run, PullRequestRef pr)
    {
        var files = await run.CodeHost.ListPullRequestFilesAsync(pr, run.Ct);
        return files.Ok ? files.Value : null;
    }

    static bool Same(PullRequestRecord a, PullRequestRecord b) =>
        JsonSerializer.Serialize(a with { ReadAt = default }, ActionRun.JsonOptions) == JsonSerializer.Serialize(b with { ReadAt = default }, ActionRun.JsonOptions);

    /// <summary>
    /// The title of the run's pull request when the step gives none (E1): its tracker issue's title and key,
    /// <c>Add DELETE /widgets/{id} (#200)</c> (a GitHub key <c>owner/repo#200</c> shortened to <c>#200</c>, a Jira key as it
    /// is); with no issue, or one that does not read, <c>&lt;level&gt;: &lt;run&gt;</c>, the level from the run's record,
    /// else its template.
    /// </summary>
    public static async Task<string> TitleAsync(ActionRun run)
    {
        if (run.Scope.TrackerIssue is { } key && run.Optional<ITrackerProvider>("tracker") is { } tracker)
        {
            var issue = await tracker.GetIssueAsync(new IssueRef(key), run.Ct);
            if (issue is { Ok: true, Value.Title: { Length: > 0 } title })
                return $"{title.Trim()} ({ShortKey(key)})";
            run.Events.Add($"could not read {key} for the pull request's title ({issue.Error?.Reason ?? "no title"}): titled by the run instead");
        }

        var records = run.Providers.Required<IRecordStoreProvider>("record_store");
        var runId = run.Call.Step.RunId;
        var level = (await records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = runId }, run.Ct)) is { Ok: true } read
            ? read.Value!.FirstOrDefault()?.Level
            : null;
        return $"{level ?? run.Call.Template()}: {runId}";
    }

    /// <summary>An issue key as a title shows it: <c>#200</c> for GitHub's <c>owner/repo#200</c>, else the key.</summary>
    public static string ShortKey(string key) => key.LastIndexOf('#') is var at and > 0 ? key[at..] : key;
}
