using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// A planner model that authors a plan patch from the readable form.
///
/// <para>
/// review 11 C2 asked for this, and review 14 A8 says why it matters: the
/// earlier measurement generated its patches in code by cycling targets, so the
/// 25% and 50% failure rates it reported were floor F3 refusals on the
/// protected <c>design</c> step, not planner reliability. A planner that reads
/// the form and chooses its own target can be measured; a loop that cycles
/// targets cannot.
/// </para>
///
/// <para>
/// The model is asked for JSON and nothing else. Its answer is parsed
/// defensively: a patch that cannot be parsed is reported as
/// <c>unparseable</c>, which is a real planner failure and is counted as one,
/// rather than being silently retried until it works.
/// </para>
/// </summary>
public sealed class PlannerModel
{
    private const string SystemPrompt =
        """
        You are a workflow planner. You are given a plan in readable form and a
        change to make. You reply with a JSON object and nothing else — no prose,
        no markdown fences.

        The JSON object has this shape:
        {"op": "insert_after" | "add_step_change_route", "target": "<step id>", "new_step_id": "<id>"}

        Rules:
        - "target" must be a step id that appears in the plan.
        - "new_step_id" must be a new id that does not appear in the plan.
        - Steps marked [protected] may not be changed. Do not choose one as the
          target.
        - "insert_after" inserts the new step immediately after the target and
          rewires the target's routes to it.
        - "add_step_change_route" adds the step and then explicitly points the
          target's completed route at it.
        """;

    /// <summary>The patch shape, for runtimes that support structured output.</summary>
    private const string PatchSchema =
        """
        {"type":"object","required":["op","target","new_step_id"],"properties":{
          "op":{"type":"string","enum":["insert_after","add_step_change_route"]},
          "target":{"type":"string"},"new_step_id":{"type":"string"}}}
        """;

    private readonly ModelClient client;

    public PlannerModel(ModelClient client) => this.client = client;

    /// <summary>
    /// Asks the planner to author one patch in the given form. Returns the
    /// patch, or an error naming why it could not be authored.
    /// </summary>
    public async Task<ProviderResult<PlanPatch>> AuthorAsync(
        Plan plan, string form, string intent, CancellationToken ct)
    {
        var readable = ReadableForm.Render(plan);
        var existing = string.Join(", ", plan.Steps.Select(s => s.Id));

        var userPrompt =
            $"""
            Plan in readable form:

            {readable}

            Existing step ids: {existing}

            Change to make: {intent}

            Author the change using the "{form}" form. Reply with the JSON object only.
            """;

        var response = await client.CompleteAsync(
            new ModelRequest(SystemPrompt, userPrompt, JsonSchema: PatchSchema), ct).ConfigureAwait(false);

        if (!response.IsOk)
            return ProviderResult<PlanPatch>.Fail(response.Error!);

        var text = response.Value!.Text.Trim();
        var parsed = ParsePatch(text);
        if (parsed is null)
        {
            return ProviderResult<PlanPatch>.Work(
                "unparseable", $"the planner did not return a patch object: {Truncate(text)}");
        }

        var (op, target, newStepId) = parsed.Value;

        if (plan.ById(target) is null)
        {
            return ProviderResult<PlanPatch>.Work(
                "unknown_target", $"the planner named target '{target}', which is not in the plan");
        }

        if (plan.ById(newStepId) is not null)
        {
            return ProviderResult<PlanPatch>.Work(
                "duplicate_step_id", $"the planner reused existing step id '{newStepId}'");
        }

        var newStep = new Step
        {
            Id = newStepId,
            Kind = "notify.send",
            Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal),
        };

        var changes = new List<PatchChange>
        {
            new()
            {
                Op = PatchOps.InsertAfter,
                Target = target,
                Step = newStep,
            },
        };

        // The two-op form must do the rewiring explicitly. That is the whole
        // difference the measurement is about.
        if (op == "add_step_change_route")
        {
            changes.Add(new PatchChange
            {
                Op = PatchOps.ChangeRoute,
                Target = target,
                Outcome = Outcomes.Completed,
                To = RouteTarget.ToStep(newStepId),
            });
        }

        return ProviderResult<PlanPatch>.Ok(new PlanPatch
        {
            BaseRevision = plan.Revision,
            ProposedBy = "agent:planner",
            Reason = $"planner-authored patch, form {form}: {intent}",
            Changes = changes,
        });
    }

    /// <summary>
    /// Parses the planner's answer. Tolerates a markdown fence, because a model
    /// that wraps its JSON is still a model that authored the patch — the fence
    /// is presentation, not a different answer.
    /// </summary>
    private static (string Op, string Target, string NewStepId)? ParsePatch(string text)
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

            var op = obj["op"]?.GetValue<string>() ?? "";
            var target = obj["target"]?.GetValue<string>() ?? "";
            var newStepId = obj["new_step_id"]?.GetValue<string>() ?? "";

            if (op.Length == 0 || target.Length == 0 || newStepId.Length == 0)
                return null;

            return (op, target, newStepId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";
}
