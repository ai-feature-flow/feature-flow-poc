namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>POST /runs/{id}/answers</c>: an answer to a wait. For a gate, <see cref="Outcome"/>; for a question, the answer's
/// text as <see cref="Outcome"/>; for a patch approval, <see cref="Action"/> <c>approve</c> or <c>reject</c>; for an
/// intervention or breakpoint, a recovery <see cref="Action"/> with its <see cref="StepId"/>, <see cref="CleanUp"/>,
/// <see cref="Attempts"/> or <see cref="Status"/>. <see cref="Artifacts"/> is what the person produced, by declared name,
/// each as text. <see cref="Reason"/> is the person's comment: required for a choice that routes the step as a failure
/// (the wait's <c>reason_required</c>), optional otherwise. The actor is the authenticated caller, never the body (F8).
/// </summary>
public sealed record AnswerRequest(
    string WaitId,
    string? Outcome = null,
    string? Action = null,
    string? StepId = null,
    bool CleanUp = false,
    long Attempts = 0,
    string? Status = null,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Artifacts = null);

/// <summary>The run's reply to an answer, an abort or a session's submission; <c>recorded: ...</c> (202) while more people must answer.</summary>
public sealed record AnswerResponse(string Result);

/// <summary><c>POST /runs/{id}/abort</c>: stop the run, with the person's reason (required), and close the pull requests it opened when asked.</summary>
public sealed record AbortRequest(string Reason, bool ClosePullRequests = false);

/// <summary><c>GET /runs/{id}/findings</c>: the run's findings ledger, its children's included.</summary>
public sealed record FindingsResponse(IReadOnlyList<FindingView> Findings);

/// <summary>
/// A finding in the ledger: its ledger id and state, and the finding as last raised; the run that raised it, whether it
/// needs a person, the person who last decided it, who raised it, whether it may be marked fixed (and why not), how often
/// it was raised again and fixed, and when it last changed.
/// </summary>
public sealed record FindingView(
    string LedgerId,
    string State,
    string Rule,
    string Title,
    string Description,
    string Severity,
    bool Blocking,
    string Basis,
    FindingLocationView Location,
    string? RequiredCorrection,
    string? Proof,
    string? RunId = null,
    bool NeedsPerson = false,
    string? DecidedByPerson = null,
    FindingRaisedView? RaisedBy = null,
    bool CanMarkFixed = false,
    string? MarkFixedRefusal = null,
    int Reraises = 0,
    int Fixes = 0,
    DateTimeOffset? UpdatedAt = null);

/// <summary>Who raised a finding: the step, the review angle from the plan item, the source (<c>agent</c>, <c>check</c>, <c>person</c>) and the model.</summary>
public sealed record FindingRaisedView(string? StepId, string? Angle, string Source, string? Model);

/// <summary><c>GET /runs/{id}/findings/{finding}</c>: a finding and its history, oldest first.</summary>
public sealed record FindingDetail(FindingView Finding, IReadOnlyList<FindingEventView> History);

/// <summary>An event in a finding's history: its kind, the state after it, who, why, the step and revision, whether code changed, and when.</summary>
public sealed record FindingEventView(
    string Kind,
    string State,
    string Actor,
    string? Reason,
    string? StepId,
    string? Revision,
    bool? CodeChanged,
    DateTimeOffset At);

/// <summary>Where a finding is: never a line number (docs/01 section 6).</summary>
public sealed record FindingLocationView(string Repository, string File, string? Symbol, string? Snippet, string? Criterion);

/// <summary>
/// <c>POST /runs/{id}/overrides</c>: a run-time override (docs/05 section 5) by key, with its optional reason, its step and its
/// arguments by name, each as text.
/// </summary>
public sealed record OverrideRequest(string Key, string? Reason = null, string? StepId = null, IReadOnlyDictionary<string, string>? Change = null);

public sealed record OverrideResponse(string OverrideId, string Result);
