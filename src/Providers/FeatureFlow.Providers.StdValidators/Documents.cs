using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.StdValidators;

// The library shapes the standard validators read (std's artifact_types: requirements, delivery_plan, evidence_claims),
// in snake_case through ArtifactShapes.Library. Fields the schema requires are nullable here, so a document missing one
// reads, and the validator names what is missing instead of failing to read it.

public sealed record PlanScope(string? Kind, string? Id);

public sealed record PlanLink(string? Artifact, string? Requirement);

public sealed record PlanOwner(string? Kind, string? Id);

public sealed record PlanMethod(string? Id, string? Kind, string? Repository, string? Tier, IReadOnlyList<string>? AllowedTestChanges = null);

/// <summary>A requirement or an acceptance criterion: both have an id, upward links or a reason, and in a parent-written plan an owner.</summary>
public sealed record PlanItem(string? Id, string? Title, string? Behaviour, IReadOnlyList<PlanLink>? Supports = null, string? SupportsReason = null,
    PlanOwner? Owner = null, IReadOnlyList<PlanMethod>? Verification = null);

/// <summary>A <c>requirements</c> or <c>delivery_plan</c> document.</summary>
public sealed record PlanDocument(PlanScope? Scope, IReadOnlyList<PlanItem>? Requirements = null, IReadOnlyList<PlanItem>? AcceptanceCriteria = null)
{
    public IReadOnlyList<PlanItem> Items => Requirements ?? AcceptanceCriteria ?? [];

    public bool IsPlan => AcceptanceCriteria is not null;
}

public sealed record ClaimSource(string? File, string? Symbol);

public sealed record ClaimedTest(string? Repository, string? Tier, string? Id, string? Change, ClaimSource? Source = null)
{
    /// <summary>What the test's source must contain: its symbol, else the last part of its id.</summary>
    public string? Symbol => Source?.Symbol ?? Id?.Split('.', '/', ':').LastOrDefault(p => p.Length > 0);
}

public sealed record Claim(string? Criterion, string? Verification, IReadOnlyList<ClaimedTest>? Tests);

/// <summary>An <c>evidence_claims</c> document.</summary>
public sealed record ClaimsDocument(IReadOnlyList<Claim>? Claims);

/// <summary>A subject read, with the name and source it was given under.</summary>
public sealed record Read<T>(ValidationSubject Subject, T Value);

/// <summary>
/// <c>trace_report</c> (std: <c>schema: engine</c>): each requirement of the run, traced or not, with each gap named. Offered by
/// <c>requirement_trace</c>; <c>artifact.validate</c> stores it as evidence.
/// </summary>
public sealed record TraceReport(bool Traced, IReadOnlyDictionary<string, string> Repositories, IReadOnlyList<TracedRequirement> Requirements);

/// <summary>One requirement in a <see cref="TraceReport"/>: whether it traced, the tests that proved it, and the gaps.</summary>
public sealed record TracedRequirement(string Id, bool Traced, IReadOnlyList<string> Tests, IReadOnlyList<string> Gaps);
