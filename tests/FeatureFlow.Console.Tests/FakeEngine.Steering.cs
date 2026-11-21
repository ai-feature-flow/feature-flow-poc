using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Task T6's reactions: the <c>skip</c>, <c>grant_attempts</c> and <c>amend</c> overrides write what the workflow traces
/// (<c>Steering.cs</c>, <c>Patches.cs</c>), so the plan (<c>GET /runs/{id}/plan</c>) shows the change. The breakpoint
/// override is <see cref="FakeEngine"/>'s own.
/// </summary>
public sealed partial class FakeEngine
{
    partial void Steering() => On(SteerAsync);

    async Task<string?> SteerAsync(Update update)
    {
        if (update is not { Name: RunWorkflowNames.Override, Argument: OverrideArgs { Key: "skip" or "grant_attempts" or "amend" } o })
            return null;
        lock (Overrides)
            Overrides.Add(o);
        var stepId = o.StepId ?? "";
        switch (o.Key)
        {
            case "skip":
                await TraceAsync(update.RunId, "skip_set", stepId, $"{o.Change["outcome"]} by {o.Actor}: {o.Reason}");
                return "applied: skipped when the run reaches it";
            case "grant_attempts":
                await TraceAsync(update.RunId, "attempts_granted", stepId, $"{o.Change["attempts"]} attempt(s) by {o.Actor}");
                return "applied";
            default:
                await AmendAsync(update.RunId);
                return "applied";
        }
    }

    /// <summary>
    /// An accepted amendment: the next plan revision indexed, and <c>patch_applied</c> traced. The revision carries the same
    /// patches as the one before, so it loads whatever the patch said; a test reads the patch from <see cref="Overrides"/>.
    /// </summary>
    async Task AmendAsync(string runId)
    {
        var latest = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = runId }, Ct)).Unwrap()
            .Where(a => a is { StepId: "", Name: ArtifactNames.PlanRevision }).MaxBy(a => a.Execution)
            ?? throw new InvalidOperationException($"run {runId} has no plan revision to amend");
        await using var stream = (await Artifacts.GetAsync(new ArtifactRef(latest.Ref), Ct)).Unwrap();
        var source = PlanSource.Read(await new StreamReader(stream).ReadToEndAsync(Ct));
        var next = source with { Revision = source.Revision + 1 };
        var stored = (await Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(next.Write())), Ct)).Unwrap();
        await AppendAsync(new RecordBatch(Artifacts:
            [new ArtifactRecord(runId, "", next.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, Now())]));
        await TraceAsync(runId, "patch_applied", "", $"revision {next.Revision}, by human");
    }
}
