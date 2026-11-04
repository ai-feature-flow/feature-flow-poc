namespace FeatureFlow.Providers.Abstractions;

/// <summary>A message a notifications provider posted, by its channel and the provider's id for it. A thread is named by its first message.</summary>
public sealed record MessageRef(string Channel, string Id);

/// <summary>
/// The notification events of docs/06 section 3.10, by the names organisation configuration routes and filters
/// (<c>notifications.routing</c>, <c>notifications.events</c>, docs/07 section 3).
/// </summary>
public static class NotificationEvents
{
    /// <summary>A person is needed; <see cref="DecisionRequest.Kind"/> says what kind of wait.</summary>
    public const string DecisionNeeded = "decision_needed";

    public const string Resumed = "resumed";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stalled = "stalled";
    public const string Stuck = "stuck";

    public static readonly IReadOnlyList<string> All = [DecisionNeeded, Resumed, Completed, Failed, Stalled, Stuck];

    /// <summary>
    /// The de-duplication key (docs/06 section 3.10): one message per run, step, attempt and event, so a restart does not
    /// repeat it. A run-level event has no step and attempt 0. The engine keeps it in a <see cref="NotificationRecord"/>.
    /// </summary>
    public static string Key(string runId, string? stepId, long attempt, string notificationEvent) =>
        $"{runId}|{stepId ?? ""}|{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{notificationEvent}";
}

/// <summary>A message about a run: an event of <see cref="NotificationEvents"/>, a title, plain text and a link to the run in the console.</summary>
public sealed record Notification(string Event, string RunId, string Title, string Text, string? StepId = null, string? Link = null);

/// <summary>
/// A person is needed: the wait to answer (<see cref="RunId"/>, <see cref="WaitId"/>), its kind (<c>decision</c>,
/// <c>intervention</c>, <c>patchapproval</c>, <c>question</c>, a breakpoint or an action request), its decision type, the
/// prompt and the choices. An interactive provider offers the choices as buttons; the answer reaches the inbound gateway,
/// which makes it an API decision call as the mapped person, where approval rules (<c>from_chat</c>) are checked.
/// </summary>
public sealed record DecisionRequest(
    string RunId,
    string WaitId,
    string Kind,
    string DecisionType,
    string Prompt,
    IReadOnlyList<string> Choices,
    string? StepId = null,
    string? Link = null);

/// <summary>
/// The notifications provider (docs/06 section 3.10). Slack first; no chat-specific type outside its provider. A post
/// in <c>thread</c> replies in it (one thread per root run; children reply in it). <see cref="PostDecisionAsync"/> does
/// not wait for the answer. A channel the provider cannot post to is <c>not_found</c>. De-duplication is the engine's,
/// by <see cref="NotificationEvents.Key"/> and the record store, and a failed send never affects the run.
/// </summary>
public interface INotificationsProvider : IProvider
{
    Task<ProviderResult<MessageRef>> SendAsync(string channel, Notification message, MessageRef? thread, CancellationToken cancellationToken);

    Task<ProviderResult<MessageRef>> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread, CancellationToken cancellationToken);
}
