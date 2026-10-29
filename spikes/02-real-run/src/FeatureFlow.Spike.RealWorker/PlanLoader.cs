using System.Text.Json;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Loads the Spike 2 plan. The plan is <c>plans/ticket-delivery.yaml</c>, the
/// real 17-step ticket plan — not a synthetic one.
///
/// <para>
/// Spike scope: a line-oriented reader over the plan's own shape, the same
/// approach <c>Manifest</c> takes. The plan is committed and its shape is fixed;
/// what matters is that the interpreter runs the real plan, not a stand-in.
/// </para>
/// </summary>
public static class PlanLoader
{
    public static Plan LoadTicketDelivery(string ticketId)
    {
        var path = FindPlanFile();
        if (path is null)
            throw new FileNotFoundException(
                "the ticket plan was not found; looked for plans/ticket-delivery.yaml " +
                "walking up from " + AppContext.BaseDirectory);

        return Load(path, ticketId);
    }

    public static Plan Load(string path, string ticketId) =>
        Parse(File.ReadAllLines(path), ticketId);

    /// <summary>
    /// Walks up from the assembly looking for <c>plans/ticket-delivery.yaml</c>,
    /// so the plan is found whether the worker runs from the project directory,
    /// from the solution directory, or from a published output folder.
    /// </summary>
    private static string? FindPlanFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plans", "ticket-delivery.yaml");
            if (File.Exists(candidate))
                return candidate;

            // The plan lives beside src/, so check one level of src/ too.
            var viaSrc = Path.Combine(directory.FullName, "src", "plans", "ticket-delivery.yaml");
            if (File.Exists(viaSrc))
                return viaSrc;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Parses the plan's step list. Only what the interpreter needs: id, kind,
    /// protected, routes, limits, gate and inputs.
    /// </summary>
    private static Plan Parse(string[] lines, string ticketId)
    {
        var plan = new Plan { Entry = "plan_spec", Revision = 1 };
        plan.Parameters["ticket_id"] = ticketId;
        plan.Parameters["run_id"] = $"run-{ticketId}";
        plan.Parameters["scope"] = "ticket";

        Step? current = null;
        var inSteps = false;
        var inRoutes = false;
        var inInputs = false;
        var inLimits = false;
        var inGate = false;

        foreach (var raw in lines)
        {
            var stripped = raw.Split('#', 2)[0].TrimEnd();
            if (stripped.Trim().Length == 0)
                continue;

            var line = stripped.TrimEnd('\r');
            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            if (text.StartsWith("steps:", StringComparison.Ordinal))
            {
                inSteps = true;
                continue;
            }

            if (!inSteps)
                continue;

            // A new step: "- id: xxx" at the step indent.
            if (text.StartsWith("- id:", StringComparison.Ordinal))
            {
                current = new Step
                {
                    Id = text["- id:".Length..].Trim(),
                    Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                    Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal),
                };
                plan.Steps.Add(current);
                inRoutes = false;
                inInputs = false;
                inLimits = false;
                inGate = false;
                continue;
            }

            if (current is null)
                continue;

            if (text.StartsWith("kind:", StringComparison.Ordinal))
            {
                current.Kind = text["kind:".Length..].Trim();
                continue;
            }

            if (text.StartsWith("protected:", StringComparison.Ordinal))
            {
                current.Protected = bool.Parse(text["protected:".Length..].Trim());
                continue;
            }

            if (text.StartsWith("inputs:", StringComparison.Ordinal))
            {
                inInputs = true;
                inRoutes = false;
                inLimits = false;
                inGate = false;
                continue;
            }

            if (text.StartsWith("limits:", StringComparison.Ordinal))
            {
                current.Limits ??= new StepLimits();
                inLimits = text == "limits:";
                inInputs = false;
                inRoutes = false;
                inGate = false;

                if (InlineValue(text, "attempts") is { } attempts)
                    current.Limits.Attempts = int.Parse(attempts);
                if (InlineValue(text, "start_to_close") is { } startToClose)
                    current.Limits.StartToClose = startToClose;
                if (InlineValue(text, "heartbeat") is { } heartbeat)
                    current.Limits.Heartbeat = heartbeat;
                continue;
            }

            if (text.StartsWith("requires:", StringComparison.Ordinal))
            {
                var capabilities = InlineValue(text, "capabilities");
                if (capabilities is not null)
                {
                    current.Requires = new StepRequires
                    {
                        Capabilities = ParseList(capabilities),
                    };
                }
                continue;
            }

            if (text.StartsWith("gate:", StringComparison.Ordinal))
            {
                inGate = true;
                inInputs = false;
                inRoutes = false;
                inLimits = false;

                // The plan writes the gate inline: gate: { decision_type: x }.
                var inline = Between(text, "decision_type:", "}");
                if (inline is not null)
                    current.Gate = new StepGate { DecisionType = inline };

                continue;
            }

            if (text.StartsWith("routes:", StringComparison.Ordinal))
            {
                inRoutes = true;
                inInputs = false;
                inLimits = false;
                inGate = false;
                continue;
            }

            if (inGate && text.StartsWith("decision_type:", StringComparison.Ordinal))
            {
                current.Gate = new StepGate
                {
                    DecisionType = text["decision_type:".Length..].Trim(),
                };
                continue;
            }

            if (inLimits)
            {
                if (text.StartsWith("attempts:", StringComparison.Ordinal))
                {
                    current.Limits ??= new StepLimits();
                    current.Limits.Attempts = int.Parse(text["attempts:".Length..].Trim());
                }
                else if (text.StartsWith("start_to_close:", StringComparison.Ordinal))
                {
                    current.Limits ??= new StepLimits();
                    current.Limits.StartToClose = text["start_to_close:".Length..].Trim();
                }
                else if (text.StartsWith("heartbeat:", StringComparison.Ordinal))
                {
                    current.Limits ??= new StepLimits();
                    current.Limits.Heartbeat = text["heartbeat:".Length..].Trim();
                }
                continue;
            }

            if (inInputs)
            {
                var colon = text.IndexOf(':');
                if (colon > 0)
                {
                    var name = text[..colon].Trim();
                    var value = text[(colon + 1)..].Trim();

                    // A nested binding object is not read by the spike; the
                    // scalar inputs (role, write_access, mode, scope) are.
                    if (value.Length > 0 && !value.StartsWith("{", StringComparison.Ordinal))
                        current.Inputs[name] = value.Trim('"', '\'');
                }
                continue;
            }

            if (inRoutes)
            {
                var colon = text.IndexOf(':');
                if (colon <= 0)
                    continue;

                var outcome = text[..colon].Trim();
                var target = text[(colon + 1)..].Trim();

                if (target.StartsWith("{", StringComparison.Ordinal))
                {
                    // { end: rejected } — a terminal directive.
                    var end = Between(target, "end:", "}");
                    if (end is not null)
                    {
                        current.Routes[outcome] = RouteTarget.ToEnd(end);
                        continue;
                    }

                    var resume = Between(target, "resume:", "}");
                    if (resume is not null)
                    {
                        current.Routes[outcome] = RouteTarget.ToResume();
                        continue;
                    }
                }

                if (target.Length > 0)
                    current.Routes[outcome] = RouteTarget.ToStep(target);
            }
        }

        return plan;
    }

    private static string? InlineValue(string text, string name)
    {
        var marker = name + ":";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var from = at + marker.Length;
        var inList = false;
        var to = text.Length;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '[')
                inList = true;
            else if (text[i] == ']')
                inList = false;
            else if (!inList && (text[i] == ',' || text[i] == '}'))
            {
                to = i;
                break;
            }
        }

        return text[from..to].Trim();
    }

    private static List<string> ParseList(string text) => text
        .Trim().Trim('[', ']')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.Trim('"', '\''))
        .Where(value => value.Length > 0)
        .ToList();

    private static string? Between(string text, string start, string end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var from = at + start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0)
            to = text.Length;

        return text[from..to].Trim();
    }
}
