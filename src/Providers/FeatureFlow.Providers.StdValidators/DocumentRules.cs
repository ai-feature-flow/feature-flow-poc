namespace FeatureFlow.Providers.StdValidators;

public sealed record WorkListItem(string? Name, IReadOnlyList<string>? DependsOn = null);

/// <summary>A <c>work_list</c> document, read for its names and dependencies.</summary>
public sealed record WorkListDocument(IReadOnlyList<WorkListItem>? Items);

public sealed record TriageItem(string? Finding);

/// <summary>A <c>triage</c> document, read for the findings it names.</summary>
public sealed record TriageDocument(IReadOnlyList<TriageItem>? Entries);

/// <summary>
/// <c>document_rules</c>: the <c>rules</c> std's artifact types state that a schema cannot (LOG 247), over one document at a
/// time, so the engine checks them when an agent produces the document and the agent corrects them in its session
/// (docs/01 section 5.4). Rules that compare documents (owners, links) stay with the cross-artifact validators.
/// </summary>
static class DocumentRules
{
    public static async Task CheckAsync(Check c)
    {
        foreach (var plan in await c.PlansAsync(Types.Requirements, Types.DeliveryPlan))
            foreach (var item in plan.Value.Items.Where(i => i.Verification is { Count: > 1 }))
                foreach (var duplicate in item.Verification!.Where(m => !string.IsNullOrEmpty(m.Id)).GroupBy(m => m.Id).Where(g => g.Count() > 1))
                    c.Error("duplicate_id", $"{item.Id} uses method id {duplicate.Key} {duplicate.Count()} times", plan.Subject.Name, item.Id);

        foreach (var subject in c.Present(Types.WorkList))
            if (await c.ReadAsync<WorkListDocument>(subject) is { } list)
                WorkList(c, subject.Name, list.Items ?? []);

        foreach (var subject in c.Present(Types.Triage))
            if (await c.ReadAsync<TriageDocument>(subject) is { } triage)
                foreach (var duplicate in (triage.Entries ?? []).Where(e => !string.IsNullOrEmpty(e.Finding)).GroupBy(e => e.Finding).Where(g => g.Count() > 1))
                    c.Error("duplicate_id", $"finding {duplicate.Key} is triaged {duplicate.Count()} times; give it one entry", subject.Name, duplicate.Key);
    }

    /// <summary>Item names are unique; <c>depends_on</c> names an item of the list; the dependencies form no cycle.</summary>
    static void WorkList(Check c, string subject, IReadOnlyList<WorkListItem> items)
    {
        var names = items.Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => i.Name!).ToList();
        foreach (var duplicate in names.GroupBy(n => n).Where(g => g.Count() > 1))
            c.Error("duplicate_id", $"item name {duplicate.Key} is used {duplicate.Count()} times", subject, duplicate.Key);
        var known = names.ToHashSet(StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.Name is not null))
            foreach (var dependency in item.DependsOn ?? [])
                if (!known.Contains(dependency))
                    c.Error("unknown_dependency", $"{item.Name} depends on {dependency}, which is not an item of the list", subject, item.Name);

        var edges = items.Where(i => i.Name is not null).GroupBy(i => i.Name!)
            .ToDictionary(g => g.Key, g => g.SelectMany(i => i.DependsOn ?? []).Where(known.Contains).ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in edges.Keys.Order(StringComparer.Ordinal))
            if (Cycle(name, edges, state, []) is { } cycle)
            {
                c.Error("dependency_cycle", $"the dependencies form a cycle: {string.Join(" -> ", cycle)}", subject, cycle[0]);
                return;
            }
    }

    /// <summary>A depth-first search: 1 while on the path, 2 when finished. The first cycle found, as the path round it.</summary>
    static List<string>? Cycle(string name, Dictionary<string, List<string>> edges, Dictionary<string, int> state, List<string> path)
    {
        if (state.GetValueOrDefault(name) == 2)
            return null;
        if (state.GetValueOrDefault(name) == 1)
            return [.. path[path.IndexOf(name)..], name];
        state[name] = 1;
        path.Add(name);
        foreach (var next in edges[name])
            if (Cycle(next, edges, state, path) is { } cycle)
                return cycle;
        path.RemoveAt(path.Count - 1);
        state[name] = 2;
        return null;
    }
}
