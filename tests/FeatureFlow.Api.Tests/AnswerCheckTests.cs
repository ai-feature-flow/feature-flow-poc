using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// A10: an answer's artifacts are checked at Send against the type the wait's step declares for the chosen outcome, as the
/// engine checks an agent's (LOG 246). A bad one is refused 400 with its errors and nothing is delivered; a validator this
/// API does not offer is left to the engine.
/// </summary>
public sealed class AnswerCheckTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    const string Good = "schema: 1\nentries:\n  - { finding: F-1, state: declined, reason: \"A known limitation\" }\n";

    /// <summary>A started run of <c>a2t:templates/triage</c> waiting at <c>person_triage</c>, whose <c>triaged</c> produces a <c>std:artifact_types/triage</c>.</summary>
    static async Task<string> WaitingAsync(DecisionWorld world)
    {
        var run = await world.StartAsync();
        await world.AppendAsync(new RecordBatch(Waits: [new WaitRecord(run.RunId, "w1", "gate", "person_triage", "findings.triage", "# Triage", ["triaged", "escalate"], true, T0)]));
        return run.RunId;
    }

    static Task<(HttpStatusCode Status, string Body)> SendAsync(DecisionWorld world, string runId, string triage, string outcome = "triaged") =>
        world.PostAsync($"runs/{runId}/answers", JsonSerializer.Serialize(new { wait_id = "w1", outcome, artifacts = new Dictionary<string, string> { ["person_triage"] = triage } }));

    static IReadOnlyDictionary<string, string>? Delivered(DecisionWorld world) =>
        world.Temporal.Updates.Select(u => u.Argument).OfType<AnswerArgs>().SingleOrDefault()?.Artifacts;

    [Fact]
    public async Task A_triage_that_does_not_fit_its_type_is_refused_with_its_errors_and_not_delivered()
    {
        await using var world = new DecisionWorld();
        var runId = await WaitingAsync(world);

        var (status, body) = await SendAsync(world, runId, "schema: 1\nentries:\n  - { finding: F-1, state: closed }\n");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var refusal = JsonSerializer.Deserialize<ErrorResponse>(body, ApiJson.Options)!;
        Assert.NotNull(refusal.Errors);
        Assert.Contains(refusal.Errors, e => e.Contains("person_triage", StringComparison.Ordinal) && e.Contains("state", StringComparison.Ordinal));
        Assert.Contains(refusal.Errors, e => e.Contains("reason", StringComparison.Ordinal));
        Assert.Empty(world.Temporal.Updates);
    }

    [Fact]
    public async Task A_triage_the_types_validator_refuses_is_refused_with_the_validators_error()
    {
        await using var world = new DecisionWorld(validators: true);
        world.Validators.Add(new ValidatorDescriptor("document_rules", "rules", [], new Dictionary<string, string>(), []),
            _ => ValidationResult.Of([new ValidationIssue("unknown_finding", ValidationSeverities.Error, "F-1 is not in the ledger", "person_triage", "entries[0]")]));
        var runId = await WaitingAsync(world);

        var (status, body) = await SendAsync(world, runId, Good);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var error = Assert.Single(JsonSerializer.Deserialize<ErrorResponse>(body, ApiJson.Options)!.Errors!);
        Assert.Contains("F-1 is not in the ledger [document_rules: unknown_finding]", error);
        Assert.Empty(world.Temporal.Updates);
    }

    [Fact]
    public async Task A_triage_that_fits_its_type_and_passes_its_validator_is_delivered()
    {
        await using var world = new DecisionWorld(validators: true);
        world.Validators.Add(new ValidatorDescriptor("document_rules", "rules", [], new Dictionary<string, string>(), []), _ => ValidationResult.Of([]));
        var runId = await WaitingAsync(world);

        var (status, body) = await SendAsync(world, runId, Good);

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(["person_triage"], Delivered(world)!.Keys);
    }

    [Fact]
    public async Task A_caller_who_may_not_answer_is_refused_before_the_answers_artifacts_are_stored_or_checked()
    {
        await using var world = new DecisionWorld(validators: true, roles: ["viewer", "steerer"]);
        var validated = 0;
        world.Validators.Add(new ValidatorDescriptor("document_rules", "rules", [], new Dictionary<string, string>(), []),
            _ => { validated++; return ValidationResult.Of([new ValidationIssue("unknown_finding", ValidationSeverities.Error, "F-1 is not in the ledger", "person_triage", "entries[0]")]); });
        var runId = await WaitingAsync(world);

        var (status, body) = await SendAsync(world, runId, Good);

        Assert.True(status == HttpStatusCode.Forbidden, body);
        Assert.Equal(0, validated);
        Assert.Empty(world.Temporal.Updates);
    }

    [Fact]
    public async Task A_triage_whose_validator_this_api_does_not_offer_is_accepted_and_left_to_the_engine()
    {
        await using var world = new DecisionWorld();
        var runId = await WaitingAsync(world);

        var (status, body) = await SendAsync(world, runId, Good);

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(["person_triage"], Delivered(world)!.Keys);
    }
}
