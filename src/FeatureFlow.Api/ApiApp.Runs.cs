using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;
using RunView = FeatureFlow.Api.Contracts.RunView;
using WaitView = FeatureFlow.Api.Contracts.WaitView;
using WaitKind = FeatureFlow.Api.Contracts.WaitKind;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Runs: start, list and show.</summary>
    static void MapRuns(WebApplication app, Handlers api)
    {
        app.MapPost("/runs", (Delegate)((HttpContext c) => api.Guard(c, caller => api.StartAsync(c, caller))))
            .Taking<StartRunRequest>().Described<StartRunResponse>("startRun", 201, 400, 403);
        app.MapGet("/runs", (Delegate)((HttpContext c) => api.Guard(c, caller => api.ListAsync(c, caller))))
            .Described<RunList>("listRuns", 200);
        app.MapGet("/runs/{id}", (HttpContext c, string id, string? after) => api.Guard(c, caller => api.ShowAsync(c, caller, id, after)))
            .Described<RunDetail>("getRun", 200, 404);
        app.MapGet("/runs/{id}/artifacts", (HttpContext c, string id) => api.Guard(c, caller => api.ListArtifactsAsync(c, caller, id)))
            .Described<ArtifactList>("listArtifacts", 200, 404);
        app.MapGet("/runs/{id}/artifacts/{reference}", (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactAsync(c, caller, id, reference)))
            .WithName("getArtifact").Produces<Stream>(200, "application/octet-stream").Produces<ErrorResponse>(401).Produces<ErrorResponse>(404);
    }

    sealed partial class Handlers
    {
        /// <summary>A plain branch name: not a full reference, no spaces or Git's forbidden characters, no empty or dot-led parts.</summary>
        static bool ValidBranch(string name) =>
            name.Length is > 0 and <= 200 && !name.StartsWith("refs/", StringComparison.Ordinal) && !name.EndsWith('/') && !name.EndsWith(".lock", StringComparison.Ordinal)
            && !name.Contains("..", StringComparison.Ordinal) && !name.Contains("@{", StringComparison.Ordinal)
            && name.All(c => c > ' ' && c != 127 && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.'));

        public async Task<IResult> StartAsync(HttpContext context, Caller caller)
        {
            var (body, malformed) = await BodyAsync<StartRunRequest>(context);
            if (body is null)
                return malformed!;
            var template = body.Template;
            if (LibraryReference.TryParse(template) is null)
                return Error(400, "template must be a template reference, such as std:templates/ticket_delivery");
            var repositories = body.Repositories ?? [];
            // Starting a run steers work in its repositories (LOG 219); a run is in at most one team's scope (LOG 217).
            if (Approval.MoreThanOneTeam(services.Organisation, repositories) is { } teams)
                return Error(400, teams);
            var scope = ScopeOf(repositories);
            if (Refuse(caller, scope, [PeopleRoles.Steerer], "start a run") is { } refused)
                return refused;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in body.Parameters ?? new Dictionary<string, string>())
            {
                // Each value is JSON text, so 3, true and "text" keep their types.
                try
                {
                    using var _ = JsonDocument.Parse(value);
                }
                catch (JsonException)
                {
                    return Error(400, $"parameters.{name} must be JSON text, such as \"\\\"ACME-12\\\"\", \"3\" or \"true\"");
                }

                parameters[name] = value;
            }

            var dryRun = body.DryRun;
            // The branch the run's work branch starts from and merges into (LOG 137); default the repository's default branch.
            var parentBranch = body.ParentBranch;
            if (parentBranch is not null && !ValidBranch(parentBranch))
                return Error(400, $"parent_branch must be a branch name, such as ff/epic/billing, not '{parentBranch}'");
            // The run's tracker issue (run.tracker_issue): its input, read from the tracker, and where declarative updates go.
            var trackerIssue = body.TrackerIssue;
            if (trackerIssue is not null && (trackerIssue.Length is 0 or > 200 || trackerIssue.Any(char.IsControl)))
                return Error(400, "tracker_issue must be a tracker key, such as ACME-12 or owner/repo#12");
            var handOver = services.Installation.ContinueAsNewEvents;
            if (body.ContinueAsNewEvents is { } requested)
            {
                // A run's own hand-over threshold is an administrator's override (LOG 87), and never below the floor.
                if (Refuse(caller, scope, [PeopleRoles.Admin], "set continue_as_new_events") is { } notAdmin)
                    return notAdmin;
                if (requested < ConfigurationReader.MinimumHandOverEvents)
                    return Error(400, $"continue_as_new_events must be at least {ConfigurationReader.MinimumHandOverEvents}");
                handOver = requested;
            }

            var lockRef = await WriteLockAsync(context.RequestAborted);
            var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
            var now = DateTimeOffset.UtcNow;
            await Records.AppendAsync(new RecordBatch(
                [new RunRecord(runId, null, template, "starting", caller.Actor, repositories, dryRun, now, null)],
                Locks: [new LockRecord(runId, lockRef, now)]), context.RequestAborted);
            var start = new RunStart(runId, template, repositories, parameters, lockRef, dryRun, caller.Actor, HandOverEvents: handOver, ParentBranch: parentBranch,
                TrackerIssue: trackerIssue);
            await services.Client.StartWorkflowAsync(RunWorkflowNames.Workflow, [start],
                new WorkflowOptions(runId, services.Installation.TaskQueue));
            return Results.Json(new StartRunResponse(runId), Json, statusCode: 201);
        }

        /// <summary>
        /// The run's lock (docs/08 sections 3 and 5.1): every content file of the organisation's libraries stored
        /// by content hash, and the lock that lists them stored as an artifact itself.
        /// </summary>
        async Task<string> WriteLockAsync(CancellationToken ct)
        {
            var locked = new List<LockedLibrary>();
            foreach (var source in services.Organisation.Libraries)
            {
                var (library, problem) = await LibrarySources.OpenAsync(source, services.Organisation.Location, ct);
                if (library is null)
                    throw new InvalidOperationException($"library {source.Name}: {problem}");
                foreach (var (_, text) in library.Files.All)
                    (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
                locked.Add(LibraryLock.Pin(library));
            }

            var lockText = JsonSerializer.Serialize(new LibraryLock(locked), Stored);
            return (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;
        }

        /// <summary>The runs the caller may read: those where it is a viewer in the run's scope (docs/05 section 3.3).</summary>
        public async Task<IResult> ListAsync(HttpContext context, Caller caller)
        {
            var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), context.RequestAborted)).Unwrap()
                .Where(r => RolesIn(caller, ScopeOf(r.Repositories)).Contains(PeopleRoles.Viewer)).Select(SummaryOf).ToList();
            return Results.Json(new RunList(runs), Json);
        }

        /// <summary>The run, if the caller is a viewer in its scope; null otherwise, so every read of a run refuses alike (404).</summary>
        async Task<RunRecord?> VisibleRunAsync(Caller caller, string id, CancellationToken ct)
        {
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap().FirstOrDefault();
            return run is not null && RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer) ? run : null;
        }

        /// <summary>
        /// The run's page (LOG 396): the run; every wait, open first, then closed ones most recently closed first, each with
        /// whether the caller may answer it now (<see cref="CheckAnswer"/>), who has answered while more must
        /// (<c>min_count</c>), and the answer that closed it; the timeline after <paramref name="after"/>; the children; every
        /// answer and override recorded; and the override keys the caller may use on it.
        /// </summary>
        public async Task<IResult> ShowAsync(HttpContext context, Caller caller, string id, string? after)
        {
            var ct = context.RequestAborted;
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, p, ct)).Unwrap().FirstOrDefault();
            if (run is null || !RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer))
                return Error(404, $"no run {id}");
            // A wait closed before any record of its opening was written has no kind (LOG 374): there is nothing to show of it.
            var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap().Where(w => w.Kind != "")
                .OrderByDescending(w => w.Open).ThenByDescending(w => w.Open ? DateTimeOffset.MinValue : w.UpdatedAt).ToList();
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = after ?? "0" }, ct)).Unwrap();
            var children = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = id }, ct)).Unwrap();
            var answers = (await Records.QueryAsync<AnswerRecord>(Queries.Answers, p, ct)).Unwrap();
            var overrides = (await Records.QueryAsync<OverrideRecord>(Queries.Overrides, p, ct)).Unwrap();
            var views = new List<WaitView>();
            foreach (var wait in waits)
            {
                var (canAnswer, minCount) = await MayAnswerAsync(caller, run, wait, ct);
                views.Add(WaitViewOf(wait, canAnswer, minCount, wait.Open && minCount > 1 ? ProgressOf(wait, answers, minCount) : null, wait.Open ? null : ClosedAnswerOf(wait, answers)));
            }

            return Results.Json(new RunDetail(ViewOf(run), views, timeline.Select(EntryOf).ToList(), children.Select(SummaryOf).ToList(),
                answers.Select(AnswerViewOf).ToList(), overrides.Select(OverrideViewOf).ToList(), OverridesAllowed(caller, ScopeOf(run.Repositories))), Json);
        }

        /// <summary>
        /// The run-time override keys the caller may use in the scope, variants included, as <c>POST /runs/{id}/overrides</c> and
        /// <c>POST /runs/{id}/abort</c> would authorise them. Answering and recovering are not listed: a wait's <c>can_answer</c>
        /// says whether the caller may (LOG 417).
        /// </summary>
        IReadOnlyList<string> OverridesAllowed(Caller caller, RoleScope scope)
        {
            if (caller.Actor == PeopleRoles.PolicyActor)
                return [];
            var roles = RolesIn(caller, scope);
            return Overrides.DefaultRoles.Keys.Where(k => k is not (Overrides.Answer or Overrides.AnswerQuestion or Overrides.Recover) && RolesForKey(k).Intersect(roles).Any())
                .ToList();
        }

        /// <summary>
        /// The run's own artifact index, in the order recorded, for a viewer of the run: declared artifacts, evidence, agent
        /// sessions' transcripts and prompts, and plan revisions (LOG 325 as amended by LOG 343 and 393). A child's are
        /// listed through the child, whose scope may differ.
        /// </summary>
        public async Task<IResult> ListArtifactsAsync(HttpContext context, Caller caller, string id)
        {
            if (await VisibleRunAsync(caller, id, context.RequestAborted) is null)
                return Error(404, $"no run {id}");
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Json(new ArtifactList(indexed.Where(a => a.RunId == id).Select(ArtifactViewOf).ToList()), Json);
        }

        /// <summary>
        /// An artifact of the run, for a viewer of the run: one its own index holds (what its timeline shows, its transcripts
        /// and prompts), or one a wait of the run names as consumed, such as the parent's contract a child's gate ratifies
        /// (LOG 394). A child's own artifacts are read through the child, whose scope may differ (review R5, F1).
        /// </summary>
        public async Task<IResult> ArtifactAsync(HttpContext context, Caller caller, string id, string reference)
        {
            var ct = context.RequestAborted;
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is null)
                return Error(404, $"no run {id}");
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap();
            if (!indexed.Any(a => a.RunId == id && a.Ref == reference)
                && !(await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap().Any(w => w.Consumed?.Any(c => c.Ref == reference) == true))
                return Error(404, $"no artifact {reference} in run {id}");
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), ct);
            if (!read.Ok)
                return Error(404, $"artifact {reference} cannot be read: {read.Error!.Message}");
            // Saved, never shown: a browser that navigates to an artifact neither renders nor sniffs it (review R1).
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentDisposition = "attachment";
            return Results.Stream(read.Value!, "application/octet-stream");
        }
    }

    static ArtifactView ArtifactViewOf(ArtifactRecord a) =>
        new(a.StepId, a.Execution, a.Name, a.Type, a.Ref, a.Action, a.Evidence, ArtifactKindOf(a), a.At);

    /// <summary>What an indexed artifact is, from the names the engine gives what it indexes (<see cref="ArtifactNames"/>).</summary>
    static ArtifactKind ArtifactKindOf(ArtifactRecord a) => a switch
    {
        { StepId: "", Name: ArtifactNames.PlanRevision } => ArtifactKind.PlanRevision,
        _ when a.Name.StartsWith(ArtifactNames.TranscriptType + ":", StringComparison.Ordinal) => ArtifactKind.Transcript,
        _ when a.Name.StartsWith(ArtifactNames.PromptType + ":", StringComparison.Ordinal) => ArtifactKind.Prompt,
        { Evidence: true } => ArtifactKind.Evidence,
        _ => ArtifactKind.Artifact,
    };

    // Store records are not responses: each is mapped to the view the API answers with (docs/02 section 7).

    static RunSummary SummaryOf(RunRecord r) => new(r.RunId, r.ParentRunId, r.Template, r.Status, r.Requester, r.Repositories, r.DryRun, r.UpdatedAt);

    static RunView ViewOf(RunRecord r) => new(r.RunId, r.ParentRunId, r.Template, r.Status, r.Requester, r.Repositories, r.DryRun, r.UpdatedAt, r.Detail);

    static TimelineEntry EntryOf(TimelineRecord t) => new(t.Sequence, t.Kind, t.StepId, t.Detail, t.At);

    /// <summary>
    /// A wait record as the API shows it, with whether the caller may answer it now, who has answered while more must
    /// (<c>min_count</c>), and the answer that closed it.
    /// </summary>
    static WaitView WaitViewOf(WaitRecord w, bool canAnswer, int minCount, IReadOnlyList<AnswerProgress>? progress, ClosedAnswer? answer) =>
        new(w.WaitId, KindOf(w.Kind), w.StepId, w.DecisionType, w.Prompt, w.Choices, w.Open, w.UpdatedAt,
            (w.Consumed ?? []).Select(c => new ConsumedArtifactView(c.Key, c.Name, c.Ref)).ToList(), canAnswer, minCount, progress, answer);

    /// <summary>The answer a person gave, as recorded (JSON <see cref="AnswerArgs"/>), or null for text that is not one (an abort's reason).</summary>
    static AnswerArgs? ArgsOf(string recorded)
    {
        try
        {
            return JsonSerializer.Deserialize<AnswerArgs>(recorded, Stored);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What an answer chose: its outcome, else its action (an intervention's recovery, a patch approval's).</summary>
    static string ChoiceOf(AnswerArgs args) => args.Outcome ?? args.Action ?? "";

    /// <summary>Whether a recorded result is the run's reply to a delivered answer, not <c>sent</c>, <c>pending: ...</c> or <c>refused: ...</c>.</summary>
    static bool Delivered(string result) =>
        result != "sent" && !result.StartsWith(Pending, StringComparison.Ordinal) && !result.StartsWith("refused: ", StringComparison.Ordinal);

    /// <summary>The answer that closed the wait: the last one the run replied to, or null if it closed otherwise (a timeout, steering).</summary>
    static ClosedAnswer? ClosedAnswerOf(WaitRecord w, IReadOnlyList<AnswerRecord> answers) =>
        answers.LastOrDefault(a => a.WaitId == w.WaitId && Delivered(a.Result)) is { } a && ArgsOf(a.Answer) is { } args
            ? new ClosedAnswer(args.Actor, ChoiceOf(args), a.Reason ?? args.Reason, a.At)
            : null;

    /// <summary>An answer record as history shows it; an abort's text is its reason.</summary>
    static AnswerView AnswerViewOf(AnswerRecord a) =>
        a.WaitId == "abort" ? new AnswerView(a.WaitId, a.Actor, "abort", a.Reason ?? a.Answer, a.Result, a.At)
        : ArgsOf(a.Answer) is { } args ? new AnswerView(a.WaitId, a.Actor, ChoiceOf(args), a.Reason ?? args.Reason, a.Result, a.At)
        : new AnswerView(a.WaitId, a.Actor, "", a.Reason, a.Result, a.At);

    static OverrideView OverrideViewOf(OverrideRecord o) => new(o.OverrideId, o.Key, o.Actor, o.Reason, o.StepId, o.Change, o.Result, o.At);

    sealed partial class Handlers
    {
        /// <summary>
        /// Who has answered an open wait each way while it needs <paramref name="needed"/> people (LOG 221): its pending answers,
        /// grouped as <see cref="SameWay"/> counts them, each person once.
        /// </summary>
        static IReadOnlyList<AnswerProgress> ProgressOf(WaitRecord w, IReadOnlyList<AnswerRecord> answers, int needed)
        {
            var ways = new List<(AnswerArgs Way, List<string> Actors)>();
            foreach (var a in answers.Where(a => a.WaitId == w.WaitId && a.Result.StartsWith(Pending, StringComparison.Ordinal)))
            {
                if (ArgsOf(a.Answer) is not { } args)
                    continue;
                if (ways.FindIndex(g => SameWay(a.Answer, g.Way)) is var i and >= 0)
                {
                    if (!ways[i].Actors.Contains(a.Actor, StringComparer.Ordinal))
                        ways[i].Actors.Add(a.Actor);
                }
                else
                {
                    ways.Add((args, [a.Actor]));
                }
            }

            return ways.Select(g => new AnswerProgress(ChoiceOf(g.Way), g.Actors, needed)).ToList();
        }
    }

    /// <summary>A wait record's kind as the API names it: the record store's name (<c>patch_approval</c>) read as <see cref="WaitKind"/>.</summary>
    static WaitKind KindOf(string kind) => kind switch
    {
        "gate" => WaitKind.Gate,
        "question" => WaitKind.Question,
        "intervention" => WaitKind.Intervention,
        "patch_approval" => WaitKind.PatchApproval,
        "breakpoint" => WaitKind.Breakpoint,
        "stalled" => WaitKind.Stalled,
        "stuck" => WaitKind.Stuck,
        _ => throw new InvalidOperationException($"a wait record has kind '{kind}', which the API does not know"),
    };
}
