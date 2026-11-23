using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;
using Temporalio.Exceptions;
using Step = FeatureFlow.Model.Plans.Step;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Decisions: answers to waits, and aborting a run.</summary>
    static void MapDecisions(WebApplication app, Handlers api)
    {
        app.MapPost("/runs/{id}/answers", (HttpContext c, string id) => api.Guard(c, caller => api.AnswerAsync(c, caller, id)))
            .Taking<AnswerRequest>().Described<AnswerResponse>("answer", 200, 400, 403, 404, 409, 503).Produces<AnswerResponse>(202);
        app.MapPost("/runs/{id}/abort", (HttpContext c, string id) => api.Guard(c, caller => api.AbortAsync(c, caller, id)))
            .Taking<AbortRequest>().Described<AnswerResponse>("abortRun", 200, 400, 403, 404, 409);
        app.MapGet("/runs/{id}/findings", (HttpContext c, string id) => api.Guard(c, caller => api.FindingsAsync(c, caller, id)))
            .Described<FindingsResponse>("getFindings", 200, 404);
        app.MapGet("/runs/{id}/findings/{finding}", (HttpContext c, string id, string finding) => api.Guard(c, caller => api.FindingAsync(c, caller, id, finding)))
            .Described<FindingDetail>("getFinding", 200, 404);
    }

    /// <summary>The result an answer is recorded with while it waits for more people to answer the same way (<c>min_count</c>).</summary>
    const string Pending = "pending: ";

    /// <summary>The most a person's reason may be, in UTF-8 bytes (LOG 432).</summary>
    public const int MaxReasonBytes = 4 * 1024;

    sealed partial class Handlers
    {
        public async Task<IResult> AnswerAsync(HttpContext context, Caller caller, string id)
        {
            var (body, malformed) = await BodyAsync<AnswerRequest>(context);
            if (body is null)
                return malformed!;
            var waitId = body.WaitId;
            var run = await VisibleRunAsync(caller, id, context.RequestAborted);
            var wait = run is null ? null : (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap()
                .FirstOrDefault(w => w.WaitId == waitId);
            if (run is null || wait is null || wait.Kind == "")
                return Error(404, $"no wait {waitId} in run {id}");

            var outcome = body.Outcome;
            var action = body.Action;
            if (wait.Kind == "patch_approval" && action is null && outcome is "approve" or "reject")
                (action, outcome) = (outcome, null);
            // F8: the actor is the authenticated caller; the body cannot name one.
            var args = new AnswerArgs(waitId, caller.Actor, outcome, action, body.StepId, body.CleanUp, body.Attempts, body.Status, Reason: body.Reason);
            if (body.Artifacts is { } given)
            {
                // The caller may answer before anything they sent is stored or checked (A10's validators may call providers).
                var (types, unreadable) = await DecisionTypesAsync(wait, context.RequestAborted);
                if (types is null)
                    return unreadable!;
                if (CheckAnswer(caller, run, wait, types, action).Refusal is { } refused)
                    return refused;
                // What the person produced, such as a triage of findings (LOG 248), stored and sent by reference on the one answer path.
                var (stored, problem) = await StoreAnswerArtifactsAsync(given, context.RequestAborted);
                if (problem is not null)
                    return Error(400, problem);
                if (wait.Kind == "gate" && outcome is not null && await AnswerArtifactErrorsAsync(run, wait, outcome, given, stored!, context.RequestAborted) is { Count: > 0 } errors)
                    return Results.Json(new ErrorResponse($"the answer's artifacts do not fit their types ({errors.Count} errors)", errors), Json, statusCode: 400);
                args = args with { Artifacts = stored };
            }

            return await SubmitAnswerAsync(context, caller, run, wait, args);
        }

        /// <summary>
        /// Every way a person answers a wait goes through here (docs/04 section 4.1), so approval applies to all of them: a
        /// reason where the choice needs one (<see cref="WaitRecord.ReasonRequired"/>, LOG 502), then
        /// <see cref="CheckAnswer"/> (roles, <c>from_chat</c>, the local token and <c>min_count</c>), then <c>min_count</c>
        /// itself, distinct people answering the same way before the answer is delivered, each recorded as they answer. The
        /// answer is recorded with the authenticated actor, then delivered as the <c>answer</c> update.
        /// </summary>
        public async Task<IResult> SubmitAnswerAsync(HttpContext context, Caller caller, RunRecord run, WaitRecord wait, AnswerArgs args)
        {
            var ct = context.RequestAborted;
            if (ReasonRefused(args.Reason, required: wait.ReasonRequired?.Contains(args.Outcome ?? args.Action) == true) is { } refusedReason)
                return refusedReason;
            args = args with { Reason = string.IsNullOrWhiteSpace(args.Reason) ? null : args.Reason };
            var scope = ScopeOf(run.Repositories);
            var (types, unreadable) = await DecisionTypesAsync(wait, ct);
            if (types is null)
                return unreadable!;
            var check = CheckAnswer(caller, run, wait, types, args.Action);
            if (check.Refusal is { } refused)
                return refused;
            var rule = check.Approval;
            var grant = wait.Kind == "intervention" && args.Action == Overrides.GrantAttempts;

            args = args with
            {
                Actor = caller.Actor,
                BeyondHardCeiling = grant && VariantsHeld(caller, scope, Overrides.GrantAttempts).Contains(Overrides.GrantAttemptsBeyond),
            };
            var minCount = check.MinCount;
            if (minCount > 1)
            {
                var earlier = (await Records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap()
                    .Where(a => a.WaitId == wait.WaitId && a.Result.StartsWith(Pending, StringComparison.Ordinal) && SameWay(a.Answer, args))
                    .Select(a => (a.Actor, a.Reason));
                var people = earlier.Append((caller.Actor, args.Reason)).DistinctBy(p => p.Actor, StringComparer.Ordinal).ToList();
                if (people.Count < minCount)
                {
                    var progress = $"{people.Count} of {minCount} people have answered this way ({rule.Source})";
                    await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(run.RunId, wait.WaitId, caller.Actor, JsonSerializer.Serialize(args, Stored),
                        Pending + progress, DateTimeOffset.UtcNow, args.Reason)]), ct);
                    return Results.Json(new AnswerResponse("recorded: " + progress), Json, statusCode: 202);
                }

                // Every person who answered is the actor of the delivered answer, and the reasons given are its reason.
                var reasons = people.Where(p => p.Reason is not null).Select(p => $"{p.Actor}: {p.Reason}").ToList();
                args = args with
                {
                    Actor = string.Join(", ", people.Select(p => p.Actor)),
                    Reason = reasons.Count == 0 ? null : string.Join("; ", reasons),
                };
            }

            return await DeliverAnswerAsync(run.RunId, wait.WaitId, caller.Actor, args, ct);
        }

        /// <summary>Whether a recorded answer is the same answer as <paramref name="args"/>, whoever gave it and whatever their reason.</summary>
        static bool SameWay(string recorded, AnswerArgs args)
        {
            try
            {
                return JsonSerializer.Deserialize<AnswerArgs>(recorded, Stored) is { } other
                    && JsonSerializer.Serialize(other with { Actor = "", Reason = null }, Stored) == JsonSerializer.Serialize(args with { Actor = "", Reason = null }, Stored);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// A 400 when a person gives no reason where one is <paramref name="required"/> (a choice that routes the step as a
        /// failure or ends the run, an abort, and a library override; docs/05 section 5; LOG 502), or a reason over
        /// <see cref="MaxReasonBytes"/>: it is carried in the run's state and its context for the next agent.
        /// </summary>
        static IResult? ReasonRefused(string? reason, bool required) =>
            required && string.IsNullOrWhiteSpace(reason) ? Error(400, "reason is required: a rejection, ending a run, an abort and a library override are recorded with the person's reason (docs/05 section 5)")
            : reason is not null && Encoding.UTF8.GetByteCount(reason) > MaxReasonBytes ? Error(400, $"reason may be at most {MaxReasonBytes} bytes of UTF-8 text")
            : null;

        async Task<IResult> DeliverAnswerAsync(string runId, string waitId, string recordedActor, AnswerArgs args, CancellationToken ct)
        {
            var answerText = JsonSerializer.Serialize(args, Stored);
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, "sent", DateTimeOffset.UtcNow, args.Reason)]), ct);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(runId).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, reply.Result, DateTimeOffset.UtcNow, args.Reason)]), ct);
                return Results.Json(new AnswerResponse(reply.Result), Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {runId} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, "refused: " + message, DateTimeOffset.UtcNow, args.Reason)]), ct);
                return Error(409, message);
            }
        }

        /// <summary>The most an answer may carry: a person's triage or another small document, not a repository's files.</summary>
        const int AnswerArtifacts = 16, AnswerArtifactBytes = 1 << 20;

        /// <summary>
        /// What a person produced in answering, such as a triage of findings (LOG 202, 248): each artifact by declared name,
        /// given as text, stored now so the answer carries references. The decision's outcome must promise each name (the run
        /// refuses others); its content is checked by the step that reads it.
        /// </summary>
        async Task<(IReadOnlyDictionary<string, string>? Stored, string? Problem)> StoreAnswerArtifactsAsync(IReadOnlyDictionary<string, string> artifacts, CancellationToken ct)
        {
            if (artifacts.Count > AnswerArtifacts)
                return (null, $"artifacts is a map of at most {AnswerArtifacts} artifact names to their content");
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, text) in artifacts)
            {
                if (text.Trim().Length == 0)
                    return (null, $"artifact {name} must be given as text");
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                if (bytes.Length > AnswerArtifactBytes)
                    return (null, $"artifact {name} is {bytes.Length} bytes; an answer's artifacts are at most {AnswerArtifactBytes}");
                stored[name] = (await Artifacts.PutAsync(new MemoryStream(bytes), ct)).Unwrap().Id;
            }

            return (stored, null);
        }

        /// <summary>
        /// Each artifact of a gate's answer checked against the type the wait's step declares for it under <paramref name="outcome"/>
        /// (A10), as the engine checks what an agent reports (LOG 246): it reads in its format, fits its schema or Markdown
        /// structure, and passes the type's validators, with the run's ledger. A validator this API's providers do not offer, or
        /// one that fails, is left to the engine: the content is still checked against the type. An artifact with no declared
        /// type, or a plan that does not load, is not checked here.
        /// </summary>
        async Task<IReadOnlyList<string>> AnswerArtifactErrorsAsync(RunRecord run, WaitRecord wait, string outcome, IReadOnlyDictionary<string, string> texts,
            IReadOnlyDictionary<string, string> stored, CancellationToken ct)
        {
            if ((await LoadPlanAsync(run, null, null, ct)).Plan is not { } loaded || loaded.Plan.StepById(wait.StepId) is not { } step)
                return [];
            var success = step.Outcomes().Any(o => o.Name == outcome && o.Class == OutcomeClass.Success);
            IReadOnlyList<FindingRecord>? ledger = null;
            var errors = new List<string>();
            foreach (var (name, text) in texts)
            {
                if (step.Produces.FirstOrDefault(p => p.Name == name && (p.Outcome == outcome || (p.Outcome is null && success)))?.Type is not { Reference: { } reference } declared
                    || loaded.Libraries.Resolve(reference)?.ArtifactType(reference.Name) is not { } type)
                    continue;
                try
                {
                    if (type.Validators.Count > 0)
                        ledger ??= (await Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap();
                    errors.AddRange(await ArtifactTypeCheck.CheckAsync(type, name, declared.Text, name, text, stored[name], services.Providers, ledger ?? [], ct));
                }
                catch (ArtifactCheckException)
                {
                    errors.AddRange(ArtifactContent.Check(type, text, name).Select(e => $"artifact '{name}' ({name}): {e}"));
                }
            }

            return errors;
        }

        /// <summary>
        /// The run's findings ledger, as a person triaging it needs it: every finding of the run and its children, with its ledger
        /// id and state, whether it needs a person and who decided it, who raised it, and whether it may be marked fixed (A6).
        /// </summary>
        public async Task<IResult> FindingsAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is null)
                return Error(404, $"no run {id}");
            var ledger = (await Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap();
            var reads = new FindingReads(this, ct);
            var views = new List<FindingView>();
            foreach (var f in ledger)
                views.Add(await reads.ViewAsync(f));
            return Results.Json(new FindingsResponse(views), Json);
        }

        /// <summary>A finding of the run's ledger (its children's included), as <see cref="FindingsAsync"/> shows it, and its history, oldest first (A6).</summary>
        public async Task<IResult> FindingAsync(HttpContext context, Caller caller, string id, string finding)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is null)
                return Error(404, $"no run {id}");
            var ledger = (await Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap();
            if (ledger.FirstOrDefault(f => f.FindingId == finding) is not { } found)
                return Error(404, $"no finding {finding} in run {id}");
            var reads = new FindingReads(this, ct);
            var history = await reads.HistoryAsync(found);
            return Results.Json(new FindingDetail(await reads.ViewAsync(found), history.Select(e =>
                new FindingEventView(e.Kind, e.State, e.Actor, e.Reason, e.StepId, e.Revision, e.CodeChanged, e.At)).ToList()), Json);
        }

        /// <summary>
        /// What showing findings reads beyond the ledger, each once: a finding's history, the people of its run (the actors of the
        /// run's answers, F8), and its run's plan, for the step that raised it.
        /// </summary>
        sealed class FindingReads(Handlers api, CancellationToken ct)
        {
            readonly Dictionary<string, IReadOnlySet<string>> people = new(StringComparer.Ordinal);
            readonly Dictionary<string, Plan?> plans = new(StringComparer.Ordinal);

            public async Task<IReadOnlyList<FindingEventRecord>> HistoryAsync(FindingRecord f) =>
                (await api.Records.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
                    new Dictionary<string, string> { ["run_id"] = f.RunId, ["finding_id"] = f.FindingId }, ct)).Unwrap();

            public async Task<IReadOnlySet<string>> PeopleAsync(string runId)
            {
                if (!people.TryGetValue(runId, out var found))
                    people[runId] = found = PeopleOf((await api.Records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = runId }, ct)).Unwrap());
                return found;
            }

            async Task<Plan?> PlanAsync(string runId)
            {
                if (!plans.TryGetValue(runId, out var plan))
                    plans[runId] = plan = await api.RunAsync(runId, ct) is { } run ? (await api.LoadPlanAsync(run, null, null, ct)).Plan?.Plan : null;
                return plan;
            }

            public async Task<FindingView> ViewAsync(FindingRecord f)
            {
                var decidedBy = FindingsLedger.PersonDecision(await HistoryAsync(f), await PeopleAsync(f.RunId));
                var (canMarkFixed, refusal) = MarkFixedOf(f);
                return FindingOf(f) with
                {
                    RunId = f.RunId,
                    NeedsPerson = FindingsLedger.AgentMayNotClear(f) && decidedBy is null,
                    DecidedByPerson = decidedBy,
                    RaisedBy = RaisedByOf(f, await PlanAsync(f.RunId)),
                    CanMarkFixed = canMarkFixed,
                    MarkFixedRefusal = refusal,
                    Reraises = f.Reraises,
                    Fixes = f.Fixes,
                    UpdatedAt = f.UpdatedAt,
                };
            }
        }

        /// <summary>A ledger entry as the API shows it: the finding as last raised, with its ledger id and state.</summary>
        static FindingView FindingOf(FindingRecord f)
        {
            var (finding, location) = (f.Finding, f.Finding.Location);
            return new FindingView(f.FindingId, f.State, finding.Rule, finding.Title, finding.Description, finding.Severity, finding.Blocking, finding.Basis,
                new FindingLocationView(location.Repository, location.File, location.Symbol, location.Snippet, location.Criterion), finding.RequiredCorrection, finding.Proof);
        }

        /// <summary>The people of a run (F8): every actor its answers name, as the API authenticated them.</summary>
        static IReadOnlySet<string> PeopleOf(IEnumerable<AnswerRecord> answers) =>
            answers.Select(a => ArgsOf(a.Answer)?.Actor).OfType<string>().ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Who raised a finding: the step of the run's plan its source names, with the review angle where the step is a
        /// <c>for_each</c> branch over angles, and the step's form as the source (<c>agent</c>, <c>person</c> for a decision,
        /// <c>check</c> for an action); <c>external</c> for a findings source provider; the source as recorded when the plan
        /// cannot be read.
        /// </summary>
        static FindingRaisedView RaisedByOf(FindingRecord f, Plan? plan)
        {
            if (plan is null)
                return new FindingRaisedView(f.Source, null, "step", f.Model);
            if (plan.StepById(f.Source) is not { } step)
                return new FindingRaisedView(null, null, "external", f.Model);
            var form = ReadableForm.Form(step) switch { "agent" => "agent", "decision" => "person", _ => "check" };
            return new FindingRaisedView(step.Id, AngleOf(step), form, f.Model);
        }

        /// <summary>The item of the innermost <c>for_each</c> branch the step is in, within its own document, where it is text known at load.</summary>
        static string? AngleOf(Step step)
        {
            for (var scope = step.Scope; scope is { Kind: ScopeKind.Branch }; scope = scope.Parent)
                if (scope is { IsForEach: true })
                    return scope.Item is YScalar { IsNull: false } item ? item.Text : null;
            return null;
        }

        /// <summary>
        /// Whether a person may mark the finding fixed, and why not (plan risk 4): the API does not read the work branch, so a
        /// finding is taken as fixable when its state last changed at another revision than the one it was raised at. The engine
        /// still refuses a <c>fixed</c> with nothing committed.
        /// </summary>
        static (bool CanMarkFixed, string? Refusal) MarkFixedOf(FindingRecord f) => f switch
        {
            { State: FindingStates.Fixed } => (false, "it is fixed already"),
            { RaisedRevision: null } => (false, $"it was raised in {f.Finding.Location.Repository}, which this run has not prepared, so no commit of this run can fix it"),
            _ when f.StateRevision != f.RaisedRevision => (true, null),
            _ => (false, "nothing has been committed since it was raised, as far as the run recorded"),
        };

        /// <summary>
        /// The organisation's libraries' decision types, read now: approval is checked at the moment of use (docs/05 section 8).
        /// They decide only a decision with no organisation rule for its type; an intervention's roles come from the
        /// <c>recover</c> override. Only then are the libraries read, and one that cannot be read refuses the answer (503)
        /// rather than leaving its approval rules out, which would let the engine's looser default decide (LOG 289).
        /// </summary>
        async Task<(List<DecisionTypeItem>? Types, IResult? Refused)> DecisionTypesAsync(WaitRecord wait, CancellationToken ct)
        {
            var types = new List<DecisionTypeItem>();
            if (wait.Kind == "intervention" || services.Organisation.Policy.RuleFor(wait.DecisionType) is not null)
                return (types, null);
            foreach (var source in services.Organisation.Libraries)
            {
                if ((await LibrarySources.OpenAsync(source, services.Organisation.Location, ct)).Library is not { } library)
                    return (null, Error(503, $"library {source.Name} cannot be read now; try again"));
                types.AddRange(library.DecisionTypes);
            }

            return (types, null);
        }

        public async Task<IResult> AbortAsync(HttpContext context, Caller caller, string id)
        {
            // A caller who does not view the run is told it does not exist, as on reads.
            if (await VisibleRunAsync(caller, id, context.RequestAborted) is not { } run)
                return Error(404, $"no run {id}");
            if (Refuse(caller, ScopeOf(run.Repositories), RolesForKey(Overrides.Abort), "abort a run") is { } refused)
                return refused;
            var (body, malformed) = await BodyAsync<AbortRequest>(context);
            if (body is null)
                return malformed!;
            if (ReasonRefused(body.Reason, required: true) is { } refusedReason)
                return refusedReason;
            var args = new AbortArgs(caller.Actor, body.Reason, body.ClosePullRequests);
            var text = args.ClosePullRequests ? args.Reason + " (close pull requests)" : args.Reason;
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, "sent", DateTimeOffset.UtcNow, args.Reason)]), context.RequestAborted);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Abort, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, reply.Result, DateTimeOffset.UtcNow, args.Reason)]), context.RequestAborted);
                return Results.Json(new AnswerResponse(reply.Result), Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {id} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, "refused: " + message, DateTimeOffset.UtcNow, args.Reason)]), context.RequestAborted);
                return Error(409, message);
            }
        }
    }
}
