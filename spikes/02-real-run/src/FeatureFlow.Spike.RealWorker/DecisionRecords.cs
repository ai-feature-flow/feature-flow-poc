using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatureFlow.Spike.RealWorker;

public enum DecisionActorKind
{
    Human,
    Policy,
    Harness,
}

public sealed record DecisionActor(
    DecisionActorKind Kind,
    string Identity,
    IReadOnlyList<string> Roles);

public sealed record DecisionRecord(
    string StepId,
    string DecisionType,
    string Decision,
    DecisionActor Actor,
    DateTimeOffset RecordedAt);

/// <summary>An append-only decision record shared by the API harness and worker.</summary>
public sealed class DecisionLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;

    public DecisionLedger(string recordDirectory, string runId)
    {
        Directory.CreateDirectory(recordDirectory);
        path = Path.Combine(recordDirectory, $"{runId}.decisions.jsonl");
    }

    public void Record(DecisionRecord record)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
    }

    public IReadOnlyList<DecisionRecord> ReadAll()
    {
        if (!File.Exists(path))
            return Array.Empty<DecisionRecord>();

        var records = new List<DecisionRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<DecisionRecord>(line, Json);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // Ignore only a trailing partial append; complete records remain usable.
            }
        }

        return records;
    }
}

public sealed record MergeAuthorizationResult(
    bool Allowed,
    string DecisionType,
    int RequiredApprovals,
    int SatisfyingApprovals,
    string Message);

/// <summary>Policy enforcement performed by scm.merge, independent of routing.</summary>
public static class MergeAuthorization
{
    public static MergeAuthorizationResult Evaluate(
        string baseBranch,
        string defaultBranch,
        IEnumerable<DecisionRecord> decisions,
        OrganisationConfiguration organisation)
    {
        var isDefaultBranch = string.Equals(
            baseBranch, defaultBranch, StringComparison.OrdinalIgnoreCase);
        var decisionType = isDefaultBranch
            ? OrganisationConfiguration.DefaultBranchMergeDecision
            : OrganisationConfiguration.TicketMergeDecision;

        if (!organisation.Approval.TryGetValue(decisionType, out var policy))
        {
            var missing = isDefaultBranch ? "floor F1" : "policy.approval";
            return new MergeAuthorizationResult(
                false, decisionType, 1, 0,
                $"REFUSED {missing}: no approval policy is configured for decision type '{decisionType}'");
        }

        var satisfying = decisions
            .Where(record => string.Equals(record.DecisionType, decisionType, StringComparison.Ordinal))
            .Where(record => string.Equals(record.Decision, "approved", StringComparison.Ordinal))
            .Where(record => ActorCounts(record.Actor, policy, isDefaultBranch))
            .Where(record => policy.Roles.Count == 0 ||
                             record.Actor.Roles.Any(role => policy.Roles.Contains(role, StringComparer.Ordinal)))
            .Select(record => record.Actor.Identity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (satisfying < policy.MinCount)
        {
            var missingCount = policy.MinCount - satisfying;
            var subject = isDefaultBranch
                ? $"floor F1: merge into default branch '{defaultBranch}'"
                : $"policy.approval: merge into integration branch '{baseBranch}'";
            return new MergeAuthorizationResult(
                false, decisionType, policy.MinCount, satisfying,
                $"REFUSED {subject} requires {policy.MinCount} human approval(s) " +
                $"for '{decisionType}'; found {satisfying}; missing human approval(s): {missingCount}");
        }

        var scope = isDefaultBranch ? $"default branch '{defaultBranch}'" : $"integration branch '{baseBranch}'";
        return new MergeAuthorizationResult(
            true, decisionType, policy.MinCount, satisfying,
            $"AUTHORIZED {scope}: {satisfying}/{policy.MinCount} human approval(s) satisfy '{decisionType}'");
    }

    private static bool ActorCounts(
        DecisionActor actor,
        ApprovalPolicy policy,
        bool isDefaultBranch)
    {
        if (actor.Kind == DecisionActorKind.Human)
            return true;

        // F1 always requires a human, even if a lower policy is misconfigured.
        if (isDefaultBranch)
            return false;

        return policy.AgentCounts && actor.Kind == DecisionActorKind.Policy;
    }
}
