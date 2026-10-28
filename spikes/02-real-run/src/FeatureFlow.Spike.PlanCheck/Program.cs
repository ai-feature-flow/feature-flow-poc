using FeatureFlow.Spike.RealWorker;

// A quick check that the real plan parses into the shape the interpreter needs.
// Not part of the ten checks: it exists so a parse bug cannot be mistaken for a
// provider failure later.

var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");

Console.WriteLine($"entry:    {plan.Entry}");
Console.WriteLine($"revision: {plan.Revision}");
Console.WriteLine($"steps:    {plan.Steps.Count}");
Console.WriteLine();

Console.WriteLine($"{"id",-20} {"kind",-22} {"prot",-5} routes");
Console.WriteLine(new string('-', 100));

foreach (var step in plan.Steps)
{
    var routes = string.Join(", ", step.Routes.Select(r =>
        $"{r.Key}->{(r.Value.Step ?? (r.Value.End is not null ? $"end:{r.Value.End}" : "resume"))}"));

    Console.WriteLine($"{step.Id,-20} {step.Kind,-22} {(step.Protected ? "yes" : "-"),-5} {routes}");
}

Console.WriteLine();

// Every route target must be a step that exists, or a directive. A dangling
// target would make the run stop silently rather than fail loudly.
var ids = plan.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
var dangling = new List<string>();

foreach (var step in plan.Steps)
{
    foreach (var (outcome, target) in step.Routes)
    {
        if (target.Step is not null && !ids.Contains(target.Step))
            dangling.Add($"{step.Id}.{outcome} -> {target.Step}");
    }
}

Console.WriteLine(dangling.Count == 0
    ? "route targets: all resolve to a step or a directive"
    : $"route targets: {dangling.Count} DANGLING: {string.Join("; ", dangling)}");

var gates = plan.Steps.Where(s => s.Gate is not null).ToList();
Console.WriteLine($"gates:    {string.Join(", ", gates.Select(g => $"{g.Id} ({g.Gate!.DecisionType})"))}");

return dangling.Count == 0 ? 0 : 1;
