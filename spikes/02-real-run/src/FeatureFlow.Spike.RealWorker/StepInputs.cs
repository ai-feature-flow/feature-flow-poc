using System.Text.Json;
using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Reads step inputs. Plan inputs arrive as <c>object?</c> and may be a
/// <see cref="JsonElement"/> after deserialisation, so a plain cast is not
/// enough — the same problem <c>ParallelInputs.FromInputs</c> solves in
/// Contracts.
/// </summary>
public static class Inputs
{
    /// <summary>The role reference, e.g. "feature-flow-default:roles/planner".</summary>
    public static string? Role(Step step) => Text(step, "role");

    /// <summary>docs/06 2.2: write access is a mechanism, roles are content.</summary>
    public static string WriteAccess(Step step) => Text(step, "write_access") ?? "branch";

    public static string? Text(Step step, string name)
    {
        if (!step.Inputs.TryGetValue(name, out var value) || value is null)
            return null;

        var text = value switch
        {
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
            _ => value.ToString(),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>A list of strings, e.g. tier tags.</summary>
    public static List<string> List(Step step, string name)
    {
        if (!step.Inputs.TryGetValue(name, out var value) || value is null)
            return new List<string>();

        if (value is JsonElement e && e.ValueKind == JsonValueKind.Array)
            return e.EnumerateArray().Select(x => x.ToString()).ToList();

        if (value is IEnumerable<object?> items)
            return items.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();

        return new List<string>();
    }
}

/// <summary>
/// Step limits. docs/06 2.1: start_to_close defaults to 4h and heartbeat to 30s.
/// </summary>
public static class Limits
{
    public static TimeSpan WallClock(Step step)
    {
        var text = step.Limits?.StartToClose;
        if (string.IsNullOrWhiteSpace(text))
            return TimeSpan.FromHours(4);

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            return TimeSpan.FromHours(4);
        }
    }
}

/// <summary>
/// The prompt for a step. docs/09 12.2: the prompt is content, rendered from the
/// task; the model and endpoint are configuration and are deliberately absent.
/// </summary>
public static class Prompt
{
    /// <summary>What the ticket asks for. The reviewer judges the diff against it.</summary>
    public const string TicketSummary =
        "Add WidgetStore.Contains(int id), returning whether the store holds a widget " +
        "with that id, together with a unit test of it.";

    public static string For(Step step, RunContext context, FileContent? manifest)
    {
        var role = Inputs.Role(step) ?? "implementer";
        var write = Inputs.WriteAccess(step);

        var manifestNote = manifest is null
            ? "feature-flow.yaml was not readable on this branch."
            : $"feature-flow.yaml on {manifest.Ref} is {manifest.SizeBytes} bytes, hash sha256:{manifest.ContentHash}.";

        // A concrete task, not an open-ended one. The step says exactly what to
        // change and exactly what to run, so the agent's work is checkable: a
        // commit either contains the change or it does not.
        var task = TaskFor(step.Id);

        // The fixer is given the findings it is fixing: the open ones the
        // reviewer raised, as recorded in the findings store.
        if (step.Id == "fix_findings")
        {
            var open = Findings.Open(context);
            var listed = open.Count == 0
                ? "(none recorded)"
                : string.Join("\n", open.Select(f =>
                    $"- {f.Id}: rule={f.Rule} path={f.Path} symbol={f.Symbol} blocking={f.Blocking} snippet={f.Snippet}"));
            task += "\n\nOpen review findings:\n" + listed;
        }

        return $"""
            You are the {role} for ticket {context.TicketId} in the repository
            {RunContext.Owner}/{RunContext.Repository}, on branch {context.Branch}.

            Working directory: {context.WorkingDirectory}
            Write access: {write}
            {manifestNote}

            YOUR TASK — do exactly this, nothing else:
            {task}

            Use your tools to edit the file. Then commit the change with git:
              git add <the file you changed>
              git commit -m "{context.TicketId}: {step.Id}"

            Do NOT push. Do NOT run tests. Do NOT create a pull request.
            When you are done, reply with one short sentence saying what you changed.
            """;
    }

    /// <summary>
    /// The concrete task for each agent step. Spike scope: the ticket is a small,
    /// real change to the fixture, so the run produces a real commit and a real
    /// diff for review to look at.
    /// </summary>
    private static string TaskFor(string stepId) => stepId switch
    {
        "plan_spec" =>
            "Create the file docs/ticket-plan.md containing a three-line plan for adding " +
            "WidgetStore.Contains(int id): the goal, the file to change, and the test to add.",

        "design" =>
            "Append a short '## Design' section to docs/ticket-plan.md describing the " +
            "Contains(int id) method: its signature, what it returns, and why it belongs on WidgetStore.",

        "implement" =>
            "Add a public method Contains(int id) to the WidgetStore class in " +
            "src/FeatureFlow.Example.Api/WidgetStore.cs. It must return true when the " +
            "store holds a widget with that id and false otherwise, as a bool, taking the " +
            "same lock as the other methods. Place it immediately after the Count method. " +
            "Change only this file; the regression test is the fixer's work if the reviewer asks for it.",

        "fix_findings" =>
            "Fix the open review findings listed below. " +
            "Every fix must come with a regression test: if " +
            "tests/FeatureFlow.Example.Api.UnitTests/WidgetStoreTests.cs has no test of " +
            "WidgetStore.Contains yet, add an xUnit [Fact] that checks Contains is false for an " +
            "unknown id and true for the id of an added widget. Keep the " +
            "[Trait(\"Category\", \"unit\")] attribute on the class.",

        _ =>
            "Append one line to docs/ticket-plan.md noting that this step ran.",
    };

    /// <summary>
    /// Whether the step's work is applied by the runtime directly rather than by
    /// the model.
    ///
    /// <para>
    /// <b>Review 14 A3: false.</b> It was true for the spike's first run,
    /// because the local Ollama models behind the runtime emitted tool calls as
    /// prose text rather than structured <c>tool_use</c> blocks, so the runtime
    /// never acted on them and <c>StepWork</c> applied canned content instead.
    /// </para>
    ///
    /// <para>
    /// That is no longer the case. <c>z-ai/glm-5.3-flash</c> through OpenRouter
    /// produces real structured tool calls, and the Copilot CLI authors real
    /// files with them, so the agent writes the change and the engine commits
    /// only what the agent wrote.
    /// </para>
    /// </summary>
    public static bool WorkIsScripted => false;
}
