using System.Text;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The readable form a planner reads and patches against (docs/12 section 4,
/// review 09 D7, review 10 B4).
///
/// <para>
/// This is the piece review 14 A8 was missing. The <c>insert_after</c>
/// measurement generated its patches in code by cycling targets, so it measured
/// floor F3 refusals rather than planner reliability. To measure what a planner
/// actually does, the planner has to read something — and it has to read the
/// same thing a human planner would: one line per step, with the routes it is
/// allowed to rewire.
/// </para>
///
/// <para>
/// Rules from docs/12 section 4: one line per step; every step's non-default
/// routes shown; the effective <c>write_access</c> shown; protected steps
/// marked; inputs hidden; no artifact hashes and no expressions. The agent
/// patches by step id.
/// </para>
/// </summary>
public static class ReadableForm
{
    /// <summary>
    /// Renders the plan. <paramref name="statusOf"/> supplies each step's
    /// status, so the form reflects where the run actually is.
    /// </summary>
    public static string Render(Plan plan, Func<string, string>? statusOf = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plan {plan.MetadataName()} rev {plan.Revision}");
        sb.AppendLine(new string('─', 78));
        sb.AppendLine($" {"#",-2} {"id",-18} {"kind",-16} {"role",-12} {"access",-8} {"status",-10} routes");

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var status = statusOf?.Invoke(step.Id) ?? "pending";
            var routes = Routes(step);

            sb.AppendLine($" {i + 1,-2} {step.Id,-18} {step.Kind,-16} " +
                          $"{RoleName(step),-12} {Inputs.WriteAccess(step),-8} {status,-10} {routes}");

            if (step.Protected)
                sb.AppendLine($" {"",-2} {"",-18} {"",-16} {"",-12} {"",-8} {"",-10} [protected]");
        }

        var protectedSteps = plan.Steps.Where(s => s.Protected).Select(s => s.Id).ToList();
        if (protectedSteps.Count > 0)
            sb.AppendLine($"Protected: {string.Join(", ", protectedSteps)}");

        return sb.ToString();
    }

    /// <summary>
    /// The routes a planner may rewire, on one compact line. Only non-default
    /// routes are shown, because the default is what the engine supplies and a
    /// planner does not need to restate it (review 10 B4).
    /// </summary>
    private static string Routes(Step step)
    {
        if (step.Routes.Count == 0)
            return "-";

        return string.Join(" ", step.Routes
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => $"{r.Key}->{Describe(r.Value)}"));
    }

    private static string Describe(RouteTarget target)
    {
        if (target.Step is { Length: > 0 } step)
            return step;

        if (target.End is { Length: > 0 } end)
            return "end:" + end;

        return "(unresolved)";
    }

    private static string RoleName(Step step)
    {
        var role = Inputs.Role(step);
        if (role is null)
            return "-";

        var slash = role.LastIndexOf('/');
        return slash >= 0 ? role[(slash + 1)..] : role;
    }

    private static string MetadataName(this Plan plan) =>
        plan.Parameters.TryGetValue("plan_name", out var value) && value is string text
            ? text
            : "ticket-delivery";
}
