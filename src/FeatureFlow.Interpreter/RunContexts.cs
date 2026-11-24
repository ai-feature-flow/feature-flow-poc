using System.Text;

namespace FeatureFlow.Interpreter;

/// <summary>
/// One thing the run has learned that the next agent should know (docs/04 section 3.5; LOG 405): an agent step's report
/// <see cref="ContextKinds.Summary"/> or a person's <see cref="ContextKinds.Answer"/>, with the step and execution it came
/// from; or a <see cref="ContextKinds.RetryCause"/>, with the step it is addressed to and that step's execution it follows
/// (LOG 513), or a person's <see cref="ContextKinds.Instruction"/> for it, likewise. <see cref="Text"/> reads on its own.
/// </summary>
public sealed record ContextEntry(string Kind, string StepId, int Execution, string Text);

/// <summary>The kinds of <see cref="ContextEntry"/>.</summary>
public static class ContextKinds
{
    public const string Summary = "summary";
    public const string Answer = "answer";
    public const string RetryCause = "retry_cause";

    /// <summary>A person's instructions for a step's next prompt (<c>instruct</c>): addressed to the step like a retry cause, and not part of the run context.</summary>
    public const string Instruction = "instruction";
}

/// <summary>The run context as an agent reads it (docs/04 section 3.5): one pure rendering, bounded.</summary>
public static class RunContexts
{
    /// <summary>The most the rendered context may be, in UTF-8 bytes; the oldest entries are dropped first.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The most entries a run keeps; the oldest goes first.</summary>
    public const int MaxEntries = 64;

    /// <summary>The most a summary entry's report summary may be, in UTF-8 bytes (LOG 19).</summary>
    public const int MaxSummaryBytes = 2 * 1024;

    /// <summary>
    /// The most any one entry's text may be, in UTF-8 bytes, so that no single entry (several people's joined reasons, a long
    /// failure) can fill <see cref="MaxBytes"/> and push every other entry out (LOG 433).
    /// </summary>
    public const int MaxEntryBytes = 4 * 1024 + 512;

    /// <summary>
    /// The run context in Markdown: <c>## Run context</c>, then <c>### From the parent run</c> when the run is a child, then one
    /// bullet per entry in order. At most <see cref="MaxBytes"/>: the oldest entries are dropped first, with a line saying how
    /// many, then the parent's section. Null when there is nothing to say.
    /// </summary>
    public static string? Render(IReadOnlyList<ContextEntry> entries, string? parentContext)
    {
        var parent = string.IsNullOrWhiteSpace(parentContext) ? null : ParentBody(parentContext);
        if (entries.Count == 0 && parent is null)
            return null;
        for (var omitted = 0; ; omitted++)
        {
            var text = Write(entries.Skip(omitted).ToList(), omitted, parent);
            if (Encoding.UTF8.GetByteCount(text) <= MaxBytes)
                return text;
            if (omitted == entries.Count)
                return parent is null ? text : Write([], omitted, null, parentOmitted: true);
        }
    }

    static string Write(IReadOnlyList<ContextEntry> entries, int omitted, string? parent, bool parentOmitted = false)
    {
        var text = new StringBuilder("## Run context\n");
        if (parent is not null)
            text.Append("\n### From the parent run\n\n").Append(parent).Append('\n');
        else if (parentOmitted)
            text.Append("\n(the parent run's context is omitted)\n");
        if (entries.Count > 0 || omitted > 0)
            text.Append(parent is null && !parentOmitted ? "\n" : "\n### This run\n\n");
        if (omitted > 0)
            text.Append($"({omitted} earlier entries omitted)\n");
        foreach (var e in entries)
            text.Append("- ").Append(e.Text.ReplaceLineEndings("\n  ")).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Why a step's execution runs (LOG 513): <c>## Why this step runs again</c>, then one bullet per retry cause addressed to
    /// it, in order. Null when there is none: a first execution, or one entered by a success.
    /// </summary>
    public static string? RenderCauses(IReadOnlyList<ContextEntry> causes)
    {
        if (causes.Count == 0)
            return null;
        var text = new StringBuilder("## Why this step runs again\n\n");
        foreach (var e in causes)
            text.Append("- ").Append(e.Text.ReplaceLineEndings("\n  ")).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// A person's instructions for a step (<c>instruct</c>): <c>## A person's instructions for this step</c>, then one bullet
    /// per instruction, in order, each saying who gave it. Null when there is none.
    /// </summary>
    public static string? RenderInstructions(IReadOnlyList<ContextEntry> instructions)
    {
        if (instructions.Count == 0)
            return null;
        var text = new StringBuilder("## A person's instructions for this step\n\n");
        foreach (var e in instructions)
            text.Append("- ").Append(e.Text.ReplaceLineEndings("\n  ")).Append('\n');
        return text.ToString();
    }

    /// <summary>A parent's rendered context without its own heading, so a grandchild's reads as one section.</summary>
    static string ParentBody(string rendered)
    {
        var body = rendered.Trim();
        return body.StartsWith("## Run context", StringComparison.Ordinal) ? body["## Run context".Length..].Trim() : body;
    }

    /// <summary>The text cut to at most <paramref name="maxBytes"/> UTF-8 bytes, marked when cut.</summary>
    public static string Bounded(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        const string cut = " [...]";
        // A character is at least one byte, so no more than this many characters fit.
        var length = Math.Min(text.Length, Math.Max(0, maxBytes - cut.Length));
        while (length > 0 && (Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) + cut.Length > maxBytes || char.IsHighSurrogate(text[length - 1])))
            length--;
        return text[..length] + cut;
    }
}

public sealed partial class RunCore
{
    /// <summary>The run context the next agent reads, rendered now, with the parent's first; null when empty.</summary>
    public string? RunContext() => RunContexts.Render(State.Context.Where(e => e.Kind != ContextKinds.Instruction).ToList(), Run.ParentContext);

    /// <summary>
    /// The retry causes addressed to a step's current execution, rendered (LOG 513), then a person's instructions for it
    /// (<see cref="RunContexts.RenderInstructions"/>): those added since its last execution that ended with a success-class
    /// outcome, so each outlasts a failed attempt until the step succeeds. An instruction given while an execution runs is
    /// for the next, which is the first to read it. They are the step's own, so a step with <c>context: none</c> reads them too.
    /// </summary>
    string? CausesOf(string stepId)
    {
        var since = State.Steps.GetValueOrDefault(stepId)?.LastSuccess ?? 0;
        List<ContextEntry> Since(string kind) => State.Context.Where(e => e.Kind == kind && e.StepId == stepId && e.Execution >= since).ToList();
        var parts = new[] { RunContexts.RenderCauses(Since(ContextKinds.RetryCause)), RunContexts.RenderInstructions(Since(ContextKinds.Instruction)) }
            .OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    void AddContext(string kind, string stepId, int execution, string text)
    {
        State.Context.Add(new ContextEntry(kind, stepId, execution, RunContexts.Bounded(text, RunContexts.MaxEntryBytes)));
        if (State.Context.Count > RunContexts.MaxEntries)
            State.Context.RemoveRange(0, State.Context.Count - RunContexts.MaxEntries);
    }
}
