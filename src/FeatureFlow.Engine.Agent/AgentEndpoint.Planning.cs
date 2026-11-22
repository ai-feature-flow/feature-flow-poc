using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Agent;

/// <summary>A query to the run's workflow failed: an engine fault, answered 503 so the client retries (LOG 60).</summary>
public sealed class EngineFaultException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// The planning tools (design note s1, s3): <c>inspect_plan</c> and <c>propose_patch</c> ask the run's workflow by query, so
/// nothing enters its history. A proposal that passes is staged in the record store as an agent event, which any replica and a
/// later session of the same attempt read; the next accepted <c>report</c> re-checks it and carries it as the report's patch,
/// which the core then applies, holds for a person or refuses as any agent patch.
/// </summary>
public static partial class AgentEndpoint
{
    public const string Self = "self";
    public const string Parent = "parent";
    public const string PatchStagedEvent = "patch_staged";
    public const string PatchRefusedEvent = "patch_refused";
    public const string PatchWithdrawnEvent = "patch_withdrawn";

    /// <summary>At most this many proposals (staged or refused) per attempt (design note s2).</summary>
    public const int MaxProposals = 5;

    /// <summary>The largest patch document accepted, in bytes of its text.</summary>
    public const int MaxPatchBytes = 64 * 1024;

    /// <summary>A staged patch, as its event's detail holds it: the target, the patch document's text and the revision it was checked against.</summary>
    public sealed record StagedPatch(string Target, string Patch, int Revision);

    /// <summary>
    /// <c>inspect_plan</c>: the run's <c>agent_plan</c> answer, or for <c>parent</c> the parent's, which the child's answer names;
    /// the parent knows nothing of the child's step, so the child's authority is shown. The agent's staged proposal, if any, is added.
    /// </summary>
    public static async Task<JsonObject> InspectPlanAsync(AgentEndpointServices s, StepClaims claims, string target, CancellationToken cancellationToken)
    {
        var view = await QueryAsync(() => s.Signals.PlanAsync(claims.WorkflowId, new AgentPlanQuery(claims.StepKey, target), cancellationToken));
        if (view.Status == AgentPlanView.Forward && view.ForwardTo is { } parent)
        {
            var own = view.Authority;
            view = await QueryAsync(() => s.Signals.PlanAsync(parent, new AgentPlanQuery(claims.WorkflowId, "child"), cancellationToken));
            if (own is not null)
                view = view with
                {
                    Authority = own with { RequireReview = view.Authority?.RequireReview ?? own.RequireReview, AllowedRoles = view.Authority?.AllowedRoles ?? own.AllowedRoles },
                };
        }

        var answer = JsonSerializer.SerializeToNode(view, Json)!.AsObject();
        if (await StagedAsync(s, claims, cancellationToken) is { } staged)
            answer["staged"] = new JsonObject
            {
                ["target"] = staged.Target, ["revision"] = staged.Revision, ["patch"] = JsonNode.Parse(staged.Patch),
            };
        return answer;
    }

    /// <summary>
    /// <c>propose_patch</c>: checks the proposal against the live plan by query and stages it, refuses it with the checks' messages,
    /// or withdraws the staged one. A later proposal replaces the staged one; a refused one leaves it.
    /// </summary>
    public static async Task<PatchReply> ProposePatchAsync(AgentEndpointServices s, StepClaims claims, JsonElement input, CancellationToken cancellationToken)
    {
        PatchRequest? request;
        try
        {
            request = input.Deserialize<PatchRequest>(Json);
        }
        catch (JsonException e)
        {
            return new PatchReply(PatchReply.Refused, null, [], [e.Message], 0, $"The proposal does not read: {e.Message}");
        }

        var events = (await s.Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken)).Unwrap();
        var staged = Staged(events);
        if (request is { Withdraw: true })
        {
            await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, PatchWithdrawnEvent, "", DateTimeOffset.UtcNow)]));
            return new PatchReply(PatchReply.Withdrawn, null, [], [], staged?.Revision ?? 0,
                staged is null ? "Nothing was staged; nothing is now." : "Withdrawn: no change to the plan is staged.");
        }

        if (events.Count(e => e.Kind is PatchStagedEvent or PatchRefusedEvent) >= MaxProposals)
            return new PatchReply(PatchReply.Limit, null, [], [], staged?.Revision ?? 0,
                $"You have made the {MaxProposals} proposals this attempt allows. " + StillStaged(staged, "Finish your work and call report."));
        var target = request?.Target ?? Self;
        if (request is null || target is not (Self or Parent))
            return await RefuseAsync(s, claims, staged, 0, [$"target must be {Self} or {Parent}, not '{target}'"]);
        if (target == Self && request.ResumeAt is not { Length: > 0 })
            return await RefuseAsync(s, claims, staged, request.BaseRevision, ["resume_at is required for target self: the step the run continues at once the change applies"]);

        var text = PatchText(claims, request);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxPatchBytes)
        {
            // Recorded as a refusal, so it counts towards the proposals an attempt allows.
            var why = $"The proposal is larger than {MaxPatchBytes / 1024} KB.";
            await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, PatchRefusedEvent, why, DateTimeOffset.UtcNow)]));
            return new PatchReply(PatchReply.Limit, null, [], [why], request.BaseRevision,
                why + " Propose a smaller change; for a larger one report blocked and say why. " + StillStaged(staged, "Nothing is staged."));
        }
        if (Unreadable(text) is { Count: > 0 } schema)
            return await RefuseAsync(s, claims, staged, request.BaseRevision, schema);

        var preview = await PreviewAsync(s, claims, target, text, cancellationToken);
        switch (preview.Status)
        {
            case PatchPreview.Applies or PatchPreview.NeedsApproval:
                var detail = JsonSerializer.Serialize(new StagedPatch(target, text, preview.Revision), Json);
                await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, PatchStagedEvent, detail, DateTimeOffset.UtcNow)]));
                var applies = preview.Status == PatchPreview.Applies;
                var effect = applies
                    ? $"It passes the plan's checks and applies when you report; {(target == Parent ? "the parent run decides where it continues." : $"the run then continues at {request.ResumeAt}.")}"
                    : $"When you report, a person decides it, because: {string.Join("; ", preview.Reasons)}.";
                return new PatchReply(PatchReply.Staged, preview.Status, preview.Reasons, preview.Messages, preview.Revision,
                    $"Staged against revision {preview.Revision}. {effect} Finish your work and call report with your outcome as usual.");
            case PatchPreview.Refused:
                return await RefuseAsync(s, claims, staged, preview.Revision, preview.Messages);
            default:
                return new PatchReply(PatchReply.Unavailable, null, [], preview.Messages, preview.Revision,
                    "The plan cannot be changed from this step" + (preview.Messages.Count > 0 ? ": " + string.Join("; ", preview.Messages) : ".") +
                    " Carry on with your task; if it cannot be done without the change, report blocked and say why.");
        }
    }

    async static Task<PatchReply> RefuseAsync(AgentEndpointServices s, StepClaims claims, StagedPatch? staged, int revision, IReadOnlyList<string> messages)
    {
        await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, PatchRefusedEvent, string.Join("\n", messages), DateTimeOffset.UtcNow)]));
        return new PatchReply(PatchReply.Refused, null, [], messages, revision,
            $"Refused:\n{string.Join("\n", messages.Select(m => "- " + m))}\n" + StillStaged(staged, "Nothing is staged."));
    }

    static string StillStaged(StagedPatch? staged, string otherwise) =>
        staged is null ? otherwise : $"Your earlier proposal (revision {staged.Revision}) is still staged; withdraw it with withdraw: true if it should not apply.";

    /// <summary>The patch document (docs/03 s10.1) for the agent's request, as JSON text, which reads as YAML.</summary>
    static string PatchText(StepClaims claims, PatchRequest request)
    {
        var document = new JsonObject
        {
            ["api_version"] = "feature-flow/v1",
            ["kind"] = "patch",
            ["metadata"] = new JsonObject
            {
                ["reason"] = request.Reason,
                ["proposed_by"] = new JsonObject { ["type"] = "agent", ["step_id"] = claims.StepId },
            },
            ["base_revision"] = request.BaseRevision,
        };
        if (request.ResumeAt is { Length: > 0 } resume)
            document["resume_at"] = resume;
        document["changes"] = new JsonArray((request.Changes ?? []).Select(c => JsonSerializer.SerializeToNode(c)).ToArray());
        return document.ToJsonString();
    }

    /// <summary>The patch's schema errors (check 1), so a malformed proposal is refused before the run is asked.</summary>
    static List<string> Unreadable(string text)
    {
        var d = new DiagnosticBag();
        if (YamlText.Parse(text, "patch", d) is { } node)
            PatchReader.Read(node, d);
        return d.Where(x => x.Blocks).Select(x => $"check {x.Check}: {x.Message}").ToList();
    }

    /// <summary>
    /// What the core would do with the patch now: the run's <c>patch_preview</c>, and for <c>parent</c> the parent's
    /// <c>child_patch_preview</c>, carrying the step's <c>planning.review</c>.
    /// </summary>
    static async Task<PatchPreview> PreviewAsync(AgentEndpointServices s, StepClaims claims, string target, string text, CancellationToken cancellationToken)
    {
        var preview = await QueryAsync(() => s.Signals.PreviewAsync(claims.WorkflowId, new PatchPreviewQuery(claims.StepKey, text, target == Parent), cancellationToken));
        if (preview.Status == PatchPreview.Forward && preview.ForwardTo is { } parent)
            preview = await QueryAsync(() => s.Signals.ChildPreviewAsync(parent, new ChildPatchPreviewQuery(claims.WorkflowId, claims.StepId, text, preview.ReviewRequired), cancellationToken));
        return preview;
    }

    /// <summary>
    /// The accepted report's staged patch, re-checked now: carried as the report's patch, or, when the plan moved and it no longer
    /// passes, the report is rejected for a correction turn naming the checks.
    /// </summary>
    static async Task<(StepReport? Report, IReadOnlyList<string> Errors)> WithStagedPatchAsync(AgentEndpointServices s, StepClaims claims, StepReport report,
        CancellationToken cancellationToken)
    {
        if (await StagedAsync(s, claims, cancellationToken) is not { } staged)
            return (report, []);
        var preview = await PreviewAsync(s, claims, staged.Target, staged.Patch, cancellationToken);
        if (preview.Status is PatchPreview.Applies or PatchPreview.NeedsApproval)
            return (report with { Patch = staged.Patch, PatchToParent = staged.Target == Parent }, []);
        return (null,
        [
            $"the plan change you proposed (against revision {staged.Revision}) no longer passes its checks at revision {preview.Revision}: "
            + string.Join("; ", preview.Messages.DefaultIfEmpty(preview.Status))
            + ". Inspect the plan and propose it again, or withdraw it with withdraw: true, then call report again",
        ]);
    }

    static async Task<StagedPatch?> StagedAsync(AgentEndpointServices s, StepClaims claims, CancellationToken cancellationToken) =>
        Staged((await s.Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken)).Unwrap());

    /// <summary>The last proposal staged and not since withdrawn.</summary>
    static StagedPatch? Staged(IReadOnlyList<AgentEventRecord> events) =>
        events.LastOrDefault(e => e.Kind is PatchStagedEvent or PatchWithdrawnEvent) is { Kind: PatchStagedEvent } last
            ? JsonSerializer.Deserialize<StagedPatch>(last.Detail, Json)
            : null;

    static async Task<T> QueryAsync<T>(Func<Task<T>> query)
    {
        try
        {
            return await query();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EngineFaultException("the run could not be asked about its plan: " + e.Message, e);
        }
    }
}
