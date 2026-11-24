namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>GET /runs/{id}/steps/{step}/activity</c>: what an agent step's sessions did, for one execution: the execution shown
/// (the latest unless <c>execution</c> names one), every agent execution of the step, and the execution's sessions in order.
/// </summary>
public sealed record StepActivity(int Execution, IReadOnlyList<int> Executions, IReadOnlyList<ActivitySession> Sessions);

/// <summary>
/// One agent session of the execution, as turns in the transcript's shape: <see cref="Source"/> is <c>transcript</c> once the
/// session's transcript is stored (<see cref="TranscriptRef"/>, read in full), else <c>live</c>, from the activity recorded
/// while it runs; <see cref="Truncated"/> is whether the live record reached its cap.
/// </summary>
public sealed record ActivitySession(int Session, string Source, string? TranscriptRef, bool Truncated, IReadOnlyList<TranscriptTurn> Turns);
