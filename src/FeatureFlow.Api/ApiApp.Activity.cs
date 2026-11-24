using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>An agent step's live activity: its sessions as turns, from the activity recorded while they run or their stored transcripts.</summary>
    static void MapActivity(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/steps/{step}/activity",
                (HttpContext c, string id, string step, int? execution) => api.Guard(c, caller => api.StepActivityAsync(c, caller, id, step, execution)))
            .Described<StepActivity>("getStepActivity", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// A step's activity for one execution, the latest unless <paramref name="execution"/> names one, for a viewer of the run
        /// (as <see cref="ListArtifactsAsync"/>). The executions are the step's agent executions: those that indexed a prompt. The
        /// execution's sessions are those with a prompt, a transcript or recorded activity; each is read from its stored
        /// transcript once indexed and readable, else from its records (<see cref="ActivityTurns"/>). A step the run has neither
        /// recorded nor planned is 404, as is an execution that is not an agent execution of the step; a step with none yet
        /// answers execution 0 and no sessions.
        /// </summary>
        public async Task<IResult> StepActivityAsync(HttpContext context, Caller caller, string id, string step, int? execution)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            var artifacts = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap()
                .Where(a => a.RunId == id && a.StepId == step).ToList();
            var executions = artifacts.Where(a => SessionOf(a, ArtifactNames.PromptType) is not null).Select(a => a.Execution).Distinct().Order().ToList();
            if (executions.Count == 0 && !await KnownStepAsync(run, step, ct))
                return Error(404, $"no step {step} in run {id}");
            if (execution is { } asked && !executions.Contains(asked))
                return Error(404, $"no agent execution {asked} of step {step} in run {id}");
            if (executions.Count == 0)
                return Results.Json(new StepActivity(0, [], []), Json);
            var shown = execution ?? executions[^1];

            var p = new Dictionary<string, string> { ["run_id"] = id, ["step_id"] = step, ["execution"] = shown.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var records = (await Records.QueryAsync<ActivityRecord>(Queries.Activity, p, ct)).Unwrap().Where(a => a.Execution == shown).ToList();
            var ofExecution = artifacts.Where(a => a.Execution == shown).ToList();
            var sessions = ofExecution.Select(a => SessionOf(a, ArtifactNames.PromptType) ?? SessionOf(a, ArtifactNames.TranscriptType)).OfType<int>()
                .Concat(records.Select(a => a.Session)).Distinct().Order();
            var views = new List<ActivitySession>();
            foreach (var session in sessions)
            {
                var live = records.Where(a => a.Session == session).ToList();
                var truncated = live.Any(a => a.Kind == ActivityKinds.Truncated);
                var transcript = ofExecution.LastOrDefault(a => SessionOf(a, ArtifactNames.TranscriptType) == session)?.Ref;
                views.Add(transcript is not null && await TranscriptTurnsAsync(transcript, ct) is { } read
                    ? new ActivitySession(session, "transcript", transcript, truncated, read.Turns)
                    : new ActivitySession(session, "live", transcript, truncated, ActivityTurns.Of(live)));
            }

            return Results.Json(new StepActivity(shown, executions, views), Json);
        }

        /// <summary>Whether the run has a timeline entry for the step, or its plan has the step.</summary>
        async Task<bool> KnownStepAsync(RunRecord run, string step, CancellationToken ct) =>
            (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string> { ["run_id"] = run.RunId, ["after"] = "0" }, ct)).Unwrap()
            .Any(t => t.StepId == step)
            || (await LoadPlanAsync(run, null, null, ct)).Plan?.Plan.AllSteps.Any(s => s.Id == step) == true;

        /// <summary>A stored transcript read as turns; null when it cannot be read or is not a transcript this API reads.</summary>
        async Task<TranscriptTurns?> TranscriptTurnsAsync(string reference, CancellationToken ct)
        {
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), ct);
            if (!read.Ok)
                return null;
            using var bytes = new MemoryStream();
            await using (var stream = read.Value!)
                await stream.CopyToAsync(bytes, ct);
            return TextOf(bytes.ToArray()) is { } text ? Transcripts.Read(text) : null;
        }
    }

    /// <summary>The session an agent session's artifact of <paramref name="type"/> (<c>prompt:&lt;n&gt;</c> or <c>transcript:&lt;n&gt;</c>) belongs to, else null.</summary>
    static int? SessionOf(ArtifactRecord a, string type) =>
        a.Name.StartsWith(type + ":", StringComparison.Ordinal)
        && int.TryParse(a.Name.AsSpan(type.Length + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}
