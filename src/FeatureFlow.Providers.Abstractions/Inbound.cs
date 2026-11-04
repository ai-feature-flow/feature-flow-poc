namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// A request from outside as it arrived: its headers (names compared without case), its body exactly as received, and
/// when it arrived. Transport-neutral, so a provider verifies a signed request without any web framework type.
/// </summary>
public sealed record InboundRequest(IReadOnlyDictionary<string, string> Headers, string Body, DateTimeOffset ReceivedAt)
{
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }
}

/// <summary>
/// A verified event from a code host or CI (docs/02 section 2.3): only a hint that something a wait reads may have
/// changed. <see cref="Repository"/> is <c>owner/name</c>; null for an event about no repository (a ping), which wakes
/// nothing. <see cref="DeliveryId"/> is the sender's id for this delivery, the same on a redelivery.
/// </summary>
public sealed record InboundEvent(string Source, string DeliveryId, string Event, string? Repository);

/// <summary>
/// A provider that receives signed events for the inbound gateway (docs/02 section 7). <see cref="ReadEventAsync"/>
/// verifies the request's signature and answers <c>unauthenticated</c> when it does not verify, so a forged event never
/// reaches a run.
/// </summary>
public interface IInboundEventSource
{
    /// <summary>The source name the gateway's route uses, such as <c>github</c>.</summary>
    string Source { get; }

    Task<ProviderResult<InboundEvent>> ReadEventAsync(InboundRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The person a chat user maps to: the actor recorded on the answer, and the roles or identity-provider groups the
/// API's people roles resolve (docs/05 section 3.3). Never the gateway itself.
/// </summary>
public sealed record ChatPerson(string Actor, IReadOnlyList<string> Roles, IReadOnlyList<string>? Groups);

/// <summary>
/// A person's choice on a decision message (<see cref="INotificationsProvider.PostDecisionAsync"/>), verified: the chat
/// user, the person it maps to (null when the user is not mapped, and then nothing is answered), and the wait and choice
/// the message carried.
/// </summary>
public sealed record ChatAnswer(string ChatUser, ChatPerson? Person, string RunId, string WaitId, string Choice);

/// <summary>
/// The receiving side of an interactive notifications provider (docs/06 section 3.10): turns a chat client's request
/// into a <see cref="ChatAnswer"/>, which the inbound gateway makes an ordinary API decision call as the mapped person.
/// A request that does not verify (signature, age) is <c>unauthenticated</c>; one that is not an answer is
/// <c>validation_failed</c>. Another transport (a socket the provider holds open) produces the same answers.
/// </summary>
public interface IChatInbound
{
    /// <summary>The source name the gateway's route uses, such as <c>slack</c>.</summary>
    string Source { get; }

    Task<ProviderResult<ChatAnswer>> ReadAnswerAsync(InboundRequest request, CancellationToken cancellationToken);
}
