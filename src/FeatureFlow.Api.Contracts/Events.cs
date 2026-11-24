namespace FeatureFlow.Api.Contracts;

/// <summary>
/// What changed: a run's records, one of its waits opening or closing, or a step's live activity growing; each is also the
/// frame's <c>event</c> name. <see cref="Reset"/> is the frame sent when <c>Last-Event-ID</c> is older than the feed holds: its
/// data is <c>{}</c>, and the client re-reads everything it shows.
/// </summary>
public enum ChangeKind
{
    RunChanged,
    WaitOpened,
    WaitClosed,
    StepChanged,
    Reset,
}

/// <summary>
/// One frame of <c>GET /events</c> (<c>text/event-stream</c>): a notification that a run changed, by sequence. It carries
/// ids, not content; the client re-reads the run, its attention list, or for <see cref="ChangeKind.StepChanged"/> the step
/// named by <see cref="StepId"/>.
/// </summary>
public sealed record ChangeEvent(long Sequence, string RunId, ChangeKind Kind, string? WaitId, DateTimeOffset UpdatedAt, string? StepId = null);
