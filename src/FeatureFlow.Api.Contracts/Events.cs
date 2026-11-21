namespace FeatureFlow.Api.Contracts;

/// <summary>What changed: a run's records, or one of its waits opening or closing.</summary>
public enum ChangeKind
{
    RunChanged,
    WaitOpened,
    WaitClosed,
}

/// <summary>
/// One frame of <c>GET /events</c> (<c>text/event-stream</c>): a notification that a run changed, by sequence. It carries
/// ids, not content; the client re-reads the run or its attention list.
/// </summary>
public sealed record ChangeEvent(long Sequence, string RunId, ChangeKind Kind, string? WaitId, DateTimeOffset UpdatedAt);
