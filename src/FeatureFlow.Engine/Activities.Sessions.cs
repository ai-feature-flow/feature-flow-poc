using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine;

/// <summary>
/// A session to open (docs/04 section 4.4), as the workflow resolved it from the plan: the decision's run, step,
/// execution and wait, the session's repository and branch, the run's work branch and any parent branch (where the
/// session's branch starts), and what goes into its workspace document.
/// </summary>
public sealed record SessionOpen(
    string RunId,
    string StepId,
    int Execution,
    string SessionId,
    string WaitId,
    string Repository,
    string Branch,
    string WorkBranch,
    string? ParentBranch,
    string Folder,
    IReadOnlyList<SessionDocumentFile> Files,
    string Prompt,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<string> ChecksFrom,
    string SubmitOutcome,
    string? AbandonOutcome);

/// <summary>A session opened: the revision its branch started at, and how the person reaches it.</summary>
public sealed record SessionOpened(string BaseRevision, string Instructions);

public sealed partial class RunActivities
{
    /// <summary>
    /// Opens a session (docs/04 section 4.4): makes its branch in the named repository, at the run's work branch if the
    /// run has one there, else at the parent branch; prepares it through the session surface (version 1: the local
    /// hand-off, RW18); stores the workspace document the person fetches; and records the session as prepared. Every part
    /// is idempotent, so a retry, or a run that opened it before a hand-over, finds the same branch and handle. The person
    /// is not known yet: whoever may answer the decision may take it, and the submitter is recorded on submission (LOG 236).
    /// </summary>
    [Activity]
    public async Task<SessionOpened> OpenSessionAsync(SessionOpen open)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        return await SessionStepAsync(async () =>
        {
            var vcs = services.Providers.ForRun("vcs", false) as IVcsProvider
                      ?? throw ActionStop.CannotAnswer("a session needs a VCS provider to make its branch, and none is configured");
            var repo = new RepoRef(open.Repository);
            var reference = "refs/heads/" + open.Branch;
            string? baseRevision = null;
            for (var tries = 0; baseRevision is null; tries++)
            {
                if (EngineActions.Must(await vcs.ResolveAsync(repo, reference, ct), "read the session branch") is { } existing)
                {
                    baseRevision = existing;
                    break;
                }

                var start = EngineActions.Must(await vcs.ResolveAsync(repo, "refs/heads/" + open.WorkBranch, ct), "read the work branch")
                            ?? await ParentHeadAsync(repo, open.ParentBranch, vcs, ct);
                var pushed = await vcs.PushAsync(repo, reference, start, null, null, ct);
                if (pushed is { Ok: false, Error.Reason: "base_moved" } && tries < 3)
                    continue;
                EngineActions.Must(pushed, "make the session branch");
                baseRevision = start;
            }

            var store = services.ArtifactStore;
            var context = await PutJsonAsync(store, open.Context, ct);
            var workspace = new SessionWorkspace(open.SessionId, open.RunId, open.WaitId, "", repo, open.Branch, baseRevision, open.Folder,
                open.Files.Select(f => new SessionFile(f.Path, f.Artifact, f.Type, f.SeedRef)).ToList(), open.Prompt, context, open.ChecksFrom);
            var handle = services.Providers.ForRun("session_surface", false) is ISessionSurfaceProvider surface
                ? EngineActions.Must(await surface.PrepareAsync(workspace, ct), "prepare the session")
                : new SessionHandle(open.SessionId, SessionHandOff.Instructions(workspace));

            var document = new SessionDocument(open.SessionId, open.RunId, open.WaitId, open.StepId, open.Repository, open.Branch, baseRevision, open.Folder,
                open.Files, open.Prompt, open.Context, open.ChecksFrom, open.SubmitOutcome, open.AbandonOutcome, handle.Instructions);
            var documentRef = await PutJsonAsync(store, document, ct);
            var now = DateTimeOffset.UtcNow;
            EngineActions.Must(await services.RecordStore.AppendAsync(new RecordBatch(
                AgentEvents: [new AgentEventRecord(SessionNames.EventKey(open.SessionId), SessionNames.WorkspaceEvent, documentRef, now)],
                Sessions: [new SessionRecord(open.SessionId, open.RunId, open.WaitId, open.StepId, "", open.Repository, open.Branch, baseRevision,
                    SessionStatuses.Prepared, null, null, now)]), ct), "record the session");
            return new SessionOpened(baseRevision, handle.Instructions);
        });
    }

    /// <summary>
    /// A session's decision was answered: records it <c>submitted</c>, with the submitter, the exact commit and the artifacts
    /// imported, or <c>closed</c>, with whoever answered otherwise (F8: the API's authenticated actor). The rest of the
    /// record is kept as the session opened.
    /// </summary>
    [Activity]
    public async Task EndSessionAsync(SessionRecord ended)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var store = services.RecordStore;
        var opened = (await store.QueryAsync<SessionRecord>(Queries.Session, new Dictionary<string, string> { ["session_id"] = ended.SessionId }, ct)).Unwrap().FirstOrDefault();
        var record = opened is null
            ? ended
            : opened with { Person = ended.Person, Status = ended.Status, SubmittedRevision = ended.SubmittedRevision, Artifacts = ended.Artifacts, At = DateTimeOffset.UtcNow };
        (await store.AppendAsync(new RecordBatch(Sessions: [record]), ct)).Unwrap();
    }

    async Task<string> ParentHeadAsync(RepoRef repo, string? parentBranch, IVcsProvider vcs, CancellationToken ct)
    {
        var parent = parentBranch;
        if (parent is null)
        {
            var host = services.Providers.ForRun("code_host", false) as ICodeHostProvider
                       ?? throw ActionStop.CannotAnswer("a session needs a code host provider to read the default branch, and none is configured");
            parent = EngineActions.Must(await host.GetDefaultBranchAsync(repo, ct), "read the default branch");
        }

        return EngineActions.Must(await vcs.ResolveAsync(repo, "refs/heads/" + parent, ct), "read the parent branch")
               ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no branch {parent} to start the session branch from");
    }

    static async Task<string> PutJsonAsync(IArtifactStoreProvider store, object value, CancellationToken ct) =>
        EngineActions.Must(await store.PutAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, Json)), ct), "store the session's document").Id;

    /// <summary>
    /// Runs a session's part of the work as engine actions run: an infrastructure error is retried (within the session
    /// activities' few tries), anything else ends the attempt with a reason a person reads.
    /// </summary>
    static async Task<T> SessionStepAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (ActionInfrastructureException e)
        {
            throw new ApplicationFailureException(e.Message, errorType: "infrastructure");
        }
        catch (ActionStop stop)
        {
            throw new ApplicationFailureException(stop.Message, errorType: "session", nonRetryable: true);
        }
    }
}
