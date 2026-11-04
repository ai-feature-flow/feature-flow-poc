using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The notifications provider's contract (docs/06 section 3.10): a message lands in the channel named, a reply in a thread
/// lands in that thread and not at the top level, a decision request carries what a person needs to answer it (the
/// prompt, its choices, and the run and wait it belongs to), and a channel the provider cannot post to is <c>not_found</c>
/// with nothing posted. A provider's test project subclasses it, reading posts back through its own means.
/// </summary>
public abstract class NotificationsContract : ContractBase
{
    protected abstract INotificationsProvider Notifications { get; }

    /// <summary>A channel the provider may post to.</summary>
    protected abstract string Channel { get; }

    /// <summary>A channel that does not exist for the provider.</summary>
    protected abstract string UnknownChannel { get; }

    /// <summary>The text of the messages in <paramref name="channel"/>: at its top level, or in <paramref name="thread"/>, in order.</summary>
    protected abstract Task<IReadOnlyList<string>> MessagesAsync(string channel, MessageRef? thread);

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];

    Notification Message(string text) => new(NotificationEvents.Completed, $"{unique}-run", $"{unique} completed", text);

    [Fact]
    public async Task A_message_lands_in_the_channel_it_names()
    {
        var sent = Ok(await Notifications.SendAsync(Channel, Message($"{unique} one"), null, Ct));

        Assert.Equal(Channel, sent.Channel);
        Assert.Contains(await MessagesAsync(Channel, null), m => m.Contains($"{unique} one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reply_lands_in_its_thread_and_not_at_the_top_level()
    {
        Assert.True(((NotificationCapabilities)Notifications.Describe().Capabilities).Threads, "the contract's thread test needs a provider with threads");
        var root = Ok(await Notifications.SendAsync(Channel, Message($"{unique} root"), null, Ct));

        var reply = Ok(await Notifications.SendAsync(Channel, Message($"{unique} child"), root, Ct));

        Assert.NotEqual(root, reply);
        Assert.Contains(await MessagesAsync(Channel, root), m => m.Contains($"{unique} child", StringComparison.Ordinal));
        Assert.DoesNotContain(await MessagesAsync(Channel, null), m => m.Contains($"{unique} child", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_decision_request_carries_its_prompt_choices_run_and_wait()
    {
        var request = new DecisionRequest($"{unique}-run", $"{unique}-wait", "decision", "ticket.spec_approval", $"{unique} approve the spec?", ["approved", "rejected"]);

        Ok(await Notifications.PostDecisionAsync(Channel, request, null, Ct));

        var text = Assert.Single(await MessagesAsync(Channel, null), m => m.Contains($"{unique} approve the spec?", StringComparison.Ordinal));
        Assert.All(new[] { "approved", "rejected", request.RunId, request.WaitId }, part => Assert.Contains(part, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_channel_is_not_found_and_nothing_is_posted()
    {
        Refused(await Notifications.SendAsync(UnknownChannel, Message($"{unique} lost"), null, Ct), "not_found");
        Refused(await Notifications.PostDecisionAsync(UnknownChannel, new DecisionRequest("r", "w", "decision", "t", $"{unique} lost", ["a"]), null, Ct), "not_found");

        Assert.DoesNotContain(await MessagesAsync(Channel, null), m => m.Contains($"{unique} lost", StringComparison.Ordinal));
    }
}

/// <summary>
/// The session surface's contract (docs/06 section 3.11): preparing a session answers with a handle for that session and
/// instructions that name it, preparing it again answers the same, and two sessions get distinct handles. A provider's test
/// project subclasses it.
/// </summary>
public abstract class SessionSurfaceContract : ContractBase
{
    protected abstract ISessionSurfaceProvider Surface { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];

    SessionWorkspace Workspace(string session) => new($"{unique}-{session}", $"{unique}-run", "approve_spec", "a-person", new RepoRef("ai-feature-flow/contract"),
        $"ff/session/{unique}-{session}", "rev1", "docs", [new SessionFile("docs/spec.md", "spec", "std:artifact_types/specification")], "Write the spec.", null, ["review_spec"]);

    [Fact]
    public async Task A_prepared_session_has_a_handle_whose_instructions_name_it()
    {
        var workspace = Workspace("a");

        var handle = Ok(await Surface.PrepareAsync(workspace, Ct));

        Assert.Equal(workspace.SessionId, handle.SessionId);
        Assert.Contains(workspace.SessionId, handle.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preparing_a_session_again_answers_the_same_and_another_session_differs()
    {
        var first = Ok(await Surface.PrepareAsync(Workspace("a"), Ct));

        var again = Ok(await Surface.PrepareAsync(Workspace("a"), Ct));
        var other = Ok(await Surface.PrepareAsync(Workspace("b"), Ct));

        Assert.Equal(first, again);
        Assert.NotEqual(first.Instructions, other.Instructions);
    }
}
