using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Plans;

/// <summary>Who proposed a patch: an agent step, a human, or the engine (docs/03 section 10.1).</summary>
public sealed record Proposer(string Type, string? StepId);

/// <summary>
/// One operation of a patch (docs/03 section 10.2). <see cref="Target"/> is a full step id. A step given in
/// <see cref="Step"/> is written in the scope of the target, as if in the template or fragment that holds it.
/// </summary>
public sealed record PatchOp(
    string Op,
    string Target,
    SourceLocation Location,
    string? Position = null,
    YMap? Step = null,
    string? Field = null,
    YNode? Value = null,
    string? OnOther = null,
    string? Branch = null);

/// <summary>A patch document (docs/03 section 10.1).</summary>
public sealed record PatchDocument(
    string? RunId,
    Proposer ProposedBy,
    string? Reason,
    long BaseRevision,
    string? ResumeAt,
    IReadOnlyList<PatchOp> Changes,
    SourceLocation Location);

public static class PatchReader
{
    public static readonly IReadOnlyList<string> Operations = ["insert", "replace", "remove", "set", "add_branch", "remove_branch", "chain"];

    /// <summary>The fields <c>set</c> may change (docs/03 section 10.2); <c>routes.&lt;outcome&gt;</c> besides.</summary>
    public static readonly IReadOnlyList<string> SetFields = ["with", "limits", "outputs", "consumes", "produces"];

    public static readonly IReadOnlyList<string> ProposerTypes = ["agent", "human", "engine"];

    /// <summary>Reads a patch document. Schema errors are check 1.</summary>
    public static PatchDocument? Read(YNode node, DiagnosticBag d)
    {
        if (MapReader.Of(node, d, "a patch") is not { } r)
            return null;
        var version = r.String("api_version", required: true);
        if (version is not null and not "feature-flow/v1")
            r.Error($"api_version must be feature-flow/v1, not '{version}'", r.Map["api_version"]!.Location);
        var kind = r.String("kind", required: true);
        if (kind is not null and not "patch")
            r.Error($"kind must be patch, not '{kind}'", r.Map["kind"]!.Location);

        string? runId = null, reason = null;
        var proposer = new Proposer("human", null);
        if (MapReader.Of(r.Node("metadata"), d, "metadata") is { } m)
        {
            runId = m.String("run_id");
            reason = m.String("reason");
            if (MapReader.Of(m.Node("proposed_by"), d, "metadata.proposed_by") is { } p)
            {
                proposer = new Proposer(p.Enum("type", ProposerTypes, required: true) ?? "human", p.String("step_id"));
                p.Finish();
            }

            m.Finish();
        }

        var baseRevision = r.Int("base_revision");
        if (baseRevision is null)
            r.Error("base_revision is required", r.Map.Location);
        var resumeAt = r.String("resume_at");
        var changes = new List<PatchOp>();
        foreach (var item in LibraryItemReader.Seq(r.Required("changes"), d, "changes"))
            if (ReadOp(item, d) is { } op)
                changes.Add(op);
        r.Finish();
        return new PatchDocument(runId, proposer, reason, baseRevision ?? 0, resumeAt, changes, node.Location);
    }

    static PatchOp? ReadOp(YNode node, DiagnosticBag d)
    {
        if (MapReader.Of(node, d, "a patch change") is not { } c)
            return null;
        var op = c.Enum("op", Operations, required: true);
        var target = c.String("target", required: op != "insert");
        string? position = null, field = null, onOther = null, branch = null;
        YMap? step = null;
        YNode? value = null;
        switch (op)
        {
            case "insert":
                var before = c.String("before");
                var after = c.String("after");
                if ((before is null) == (after is null))
                    c.Error("insert takes exactly one of before or after, naming the target", c.Map.Location);
                if (target is not null)
                    c.Error("insert names its target with before or after, not target", c.Map["target"]!.Location);
                position = before is not null ? "before" : "after";
                target ??= before ?? after;
                step = StepMap(c, d);
                break;
            case "replace" or "add_branch":
                step = StepMap(c, d);
                break;
            case "chain":
                step = StepMap(c, d);
                onOther = c.String("on_other", required: true);
                break;
            case "set":
                field = c.String("field", required: true);
                value = c.Required("value");
                if (field is not null && !SetFields.Contains(field) && !field.StartsWith("routes.", StringComparison.Ordinal))
                    c.Error($"set changes one of {string.Join(", ", SetFields)} or routes.<outcome>, not '{field}'", c.Map["field"]!.Location);
                break;
            case "remove_branch":
                branch = c.String("branch", required: true);
                break;
        }

        c.Finish();
        return op is null || target is null ? null : new PatchOp(op, target, c.Map.Location, position, step, field, value, onOther, branch);
    }

    static YMap? StepMap(MapReader c, DiagnosticBag d)
    {
        var node = c.Required("step");
        if (node is null or YMap)
            return node as YMap;
        c.Error("step must be a map", node.Location);
        return null;
    }
}
