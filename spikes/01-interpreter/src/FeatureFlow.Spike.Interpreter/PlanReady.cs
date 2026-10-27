using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// The pure functions of the interpreter: plan.ready and plan.route.
/// docs/01 3.1: everything outside await is a pure function of the plan and the
/// state. That is the determinism boundary.
/// </summary>
public static class PlanReady
{
    /// <summary>
    /// Steps whose joins are satisfied and whose concurrency limit allows them.
    /// docs/01 3.1: concurrency is bounded by the plan, not by the interpreter.
    /// </summary>
    public static List<Step> Ready(InterpreterState state)
    {
        var ready = new List<Step>();

        foreach (var step in state.Plan.Steps)
        {
            if (state.Cursors.Contains(step.Id))
                continue;
            if (state.StepRuns.ContainsKey(step.Id))
                continue;
            if (!IsReachable(state.Plan, step.Id))
                continue;
            if (!DependenciesSatisfied(state, step))
                continue;
            ready.Add(step);
        }

        return ready;
    }

    private static bool DependenciesSatisfied(InterpreterState state, Step step)
    {
        // A step is ready when some completed step routes to it, or it is the
        // entry, or it is a branch of a parallel whose parent has run.
        if (step.Id == state.Plan.Entry)
            return true;

        foreach (var other in state.Plan.Steps)
        {
            if (other.Kind == "parallel" &&
                ParallelInputs.FromInputs(other.Inputs).Branches.Contains(step.Id) &&
                state.StepRuns.ContainsKey(other.Id))
            {
                return true;
            }

            if (!state.StepRuns.TryGetValue(other.Id, out var run))
                continue;
            var target = ResolveRoute(other, run.Outcome, state);
            if (target?.Step == step.Id)
                return true;
        }

        return false;
    }

    private static bool IsReachable(Plan plan, string stepId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(plan.Entry);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
                continue;
            if (id == stepId)
                return true;
            var step = plan.ById(id);
            if (step is null)
                continue;
            foreach (var target in step.Routes.Values)
            {
                if (target.Step is not null)
                    queue.Enqueue(target.Step);
                foreach (var g in target.Guarded ?? new List<GuardedRoute>())
                {
                    if (g.To?.Step is not null)
                        queue.Enqueue(g.To.Step);
                }
            }
            if (step.Kind == "parallel")
            {
                foreach (var b in ParallelInputs.FromInputs(step.Inputs).Branches)
                    queue.Enqueue(b);
            }
        }
        return false;
    }

    /// <summary>
    /// Resolve an outcome to a route target. docs/09 6.1: guards in order, first
    /// true wins, and a guarded list must end with an unguarded fallback.
    /// </summary>
    public static RouteTarget? ResolveRoute(Step step, string outcome, InterpreterState state)
    {
        if (!step.Routes.TryGetValue(outcome, out var target))
            return null;

        if (target.Guarded is null)
            return target;

        foreach (var guarded in target.Guarded)
        {
            if (guarded.When is null)
                return guarded.To;
            if (EvaluateGuard(guarded.When, state))
                return guarded.To;
        }
        return null;
    }

    /// <summary>
    /// Spike scope: guards are evaluated by a tiny interpreter over the
    /// namespaces in docs/09 8.3. The real engine uses CEL (docs/09 8.1); the
    /// CelChecks project decides whether Cel.NET can do this.
    /// </summary>
    private static bool EvaluateGuard(string expression, InterpreterState state)
    {
        var text = expression.Trim();

        if (text.Contains("=="))
        {
            var parts = text.Split("==", 2);
            var left = parts[0].Trim();
            var right = parts[1].Trim().Trim('"', '\'');
            var actual = ResolvePath(left, state);
            return string.Equals(actual, right, StringComparison.Ordinal);
        }

        if (text.Contains("!="))
        {
            var parts = text.Split("!=", 2);
            var actual = ResolvePath(parts[0].Trim(), state);
            return !string.Equals(actual, parts[1].Trim().Trim('"', '\''), StringComparison.Ordinal);
        }

        if (text.StartsWith("size(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = text[5..^1];
            var comparison = inner.Split('>', 2);
            if (comparison.Length == 2)
            {
                var value = ResolvePath(comparison[0].Trim(), state);
                var count = value?.Split(',').Length ?? 0;
                return count > int.Parse(comparison[1].Trim());
            }
        }

        return false;
    }

    private static string? ResolvePath(string path, InterpreterState state)
    {
        var parts = path.Split('.');
        if (parts.Length == 0)
            return null;

        if (parts[0] == "steps" && parts.Length >= 3)
        {
            var stepId = parts[1];
            if (!state.StepRuns.TryGetValue(stepId, out var run))
                return null;
            if (parts[2] == "outcome")
                return run.Outcome;
            if (parts[2] == "outputs" && parts.Length >= 4)
                return run.Outputs.TryGetValue(parts[3], out var v) ? v : null;
            return null;
        }

        if (parts[0] == "params" && parts.Length >= 2)
        {
            return state.Plan.Parameters.TryGetValue(parts[1], out var p) ? p?.ToString() : null;
        }

        if (parts[0] == "run" && parts.Length >= 2)
        {
            return parts[1] switch
            {
                "id" => state.Plan.Parameters.TryGetValue("run_id", out var r) ? r?.ToString() : null,
                "scope" => state.Plan.Parameters.TryGetValue("scope", out var s) ? s?.ToString() : null,
                _ => null,
            };
        }

        return null;
    }
}
