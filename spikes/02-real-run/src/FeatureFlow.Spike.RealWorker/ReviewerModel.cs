using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// A reviewer model that produces a verdict from a diff and the prior findings.
///
/// <para>
/// review 14 A7: the previous verdict was "a pure function of the diff hash",
/// so R4 passed by construction — the same inputs gave the same answer because
/// there was no model in the loop to disagree. This puts a model in the loop.
/// </para>
///
/// <para>
/// The verdict is deliberately <b>not</b> forced to agree between passes. If two
/// passes on unchanged input disagree, that is the finding, and it is reported
/// rather than reconciled. A check that can only pass is not a check.
/// </para>
/// </summary>
public sealed class ReviewerModel
{
    private const string SystemPrompt =
        """
        You are a code reviewer for a merge bar. You are given a unified diff and
        the findings raised by earlier review passes.

        Reply with a JSON object and nothing else — no prose, no markdown fences:

        {"outcome": "completed" | "findings_open",
         "findings": [{"rule": "<rule id>", "path": "<file path>", "symbol": "<symbol>",
                       "snippet": "<the offending line>", "blocking": true|false}]}

        Rules:
        - Raise a finding only for something the diff actually shows, or for
          something the ticket requires that the diff does not contain.
        - A finding listed in the prior findings is already known. Raise it again
          only if the diff still shows it; if it was fixed, do not raise it.
        - A finding that was declined must not be raised again.
        - "blocking" is true only when the issue should stop the merge.
        - If the diff shows no problem, return an empty findings list and
          outcome "completed".
        """;

    /// <summary>
    /// The verdict shape, for runtimes that support structured output. The
    /// prompt states the same shape, so a runtime without it still works.
    /// </summary>
    private const string VerdictSchema =
        """
        {"type":"object","required":["outcome","findings"],"properties":{
          "outcome":{"type":"string","enum":["completed","findings_open"]},
          "findings":{"type":"array","items":{"type":"object",
            "required":["rule","path","symbol","snippet","blocking"],
            "properties":{"rule":{"type":"string"},"path":{"type":"string"},
              "symbol":{"type":"string"},"snippet":{"type":"string"},"blocking":{"type":"boolean"}}}}}}
        """;

    private readonly ModelClient client;

    public ReviewerModel(ModelClient client) => this.client = client;

    /// <summary>
    /// One review pass. <paramref name="prior"/> carries the findings earlier
    /// passes raised, and which of them were declined, so the model can
    /// stabilise rather than re-raise.
    /// </summary>
    public async Task<ProviderResult<ReviewVerdict>> ReviewAsync(
        string? diffHash,
        string diffText,
        IReadOnlyList<Finding> prior,
        IReadOnlyList<string> declinedIds,
        string stepId,
        CancellationToken ct,
        string? ticket = null)
    {
        var priorText = prior.Count == 0
            ? "(none)"
            : string.Join("\n", prior.Select(f =>
                $"- {f.Id} rule={f.Rule} path={f.Path} symbol={f.Symbol} " +
                $"blocking={f.Blocking} snippet={f.Snippet}"));

        var declinedText = declinedIds.Count == 0
            ? "(none)"
            : string.Join(", ", declinedIds);

        var userPrompt =
            $"""
            Ticket: {ticket ?? "(not given)"}

            Diff hash: {diffHash ?? "(unresolved)"}

            Prior findings:
            {priorText}

            Declined findings (never raise these again): {declinedText}

            Unified diff:

            {Truncate(diffText, 6000)}

            Review the diff. Reply with the JSON object only.
            """;

        var response = await client.CompleteAsync(
            new ModelRequest(SystemPrompt, userPrompt, JsonSchema: VerdictSchema), ct).ConfigureAwait(false);

        if (!response.IsOk)
            return ProviderResult<ReviewVerdict>.Fail(response.Error!);

        var text = response.Value!.Text.Trim();
        var parsed = ParseVerdict(text, stepId);
        if (parsed is null)
        {
            return ProviderResult<ReviewVerdict>.Work(
                "unparseable", $"the reviewer did not return a verdict object: {Truncate(text, 200)}");
        }

        var (outcome, findings) = parsed.Value;

        // A declined finding is never re-raised, whatever the model says (K4).
        // The engine enforces this rather than trusting the model, because the
        // decline path is a floor, not a suggestion.
        var raised = findings
            .Where(f => !declinedIds.Contains(f.Id, StringComparer.Ordinal))
            .ToList();

        var effectiveOutcome = raised.Count > 0 ? Outcomes.FindingsOpen : outcome;

        return ProviderResult<ReviewVerdict>.Ok(new ReviewVerdict(
            StepId: stepId,
            Outcome: effectiveOutcome,
            DiffHash: diffHash,
            PriorFindingIds: prior.Select(f => f.Id).ToList(),
            Findings: raised,
            At: DateTimeOffset.UtcNow,
            Model: response.Value!.Model,
            InputTokens: response.Value!.InputTokens,
            OutputTokens: response.Value!.OutputTokens,
            CostUsd: response.Value!.CostUsd));
    }

    private static (string Outcome, List<Finding> Findings)? ParseVerdict(string text, string stepId)
    {
        var candidate = text;

        var fence = candidate.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var rest = candidate[(fence + 3)..];
            var newline = rest.IndexOf('\n');
            if (newline >= 0)
                rest = rest[(newline + 1)..];

            var close = rest.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0)
                rest = rest[..close];

            candidate = rest.Trim();
        }

        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            var node = JsonNode.Parse(candidate[start..(end + 1)]);
            if (node is not JsonObject obj)
                return null;

            var outcome = obj["outcome"]?.GetValue<string>() ?? Outcomes.Completed;
            if (outcome is not ("completed" or "findings_open"))
                outcome = Outcomes.Completed;

            var findings = new List<Finding>();
            if (obj["findings"] is JsonArray array)
            {
                var index = 0;
                foreach (var item in array)
                {
                    if (item is not JsonObject finding)
                        continue;

                    index++;
                    findings.Add(new Finding(
                        Id: $"f-{stepId}-{index}",
                        Rule: finding["rule"]?.GetValue<string>() ?? "review/unspecified",
                        Path: finding["path"]?.GetValue<string>() ?? "",
                        Symbol: finding["symbol"]?.GetValue<string>() ?? "",
                        Snippet: finding["snippet"]?.GetValue<string>() ?? "",
                        Blocking: finding["blocking"]?.GetValue<bool>() ?? false));
                }
            }

            return (outcome, findings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
