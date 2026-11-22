using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.StdValidators;

public sealed record WorkListItem(string? Name, IReadOnlyList<string>? DependsOn = null);

/// <summary>A <c>work_list</c> document, read for its names and dependencies.</summary>
public sealed record WorkListDocument(IReadOnlyList<WorkListItem>? Items);

public sealed record TriageItem(string? Finding);

/// <summary>A <c>triage</c> document, read for the findings it names.</summary>
public sealed record TriageDocument(IReadOnlyList<TriageItem>? Entries);

public sealed record FindingsReportLocation(string? Repository, string? File);

public sealed record FindingsReportItem(string? SameAs, FindingsReportLocation? Location);

/// <summary>A <c>findings_report</c>, read for the ledger ids its findings name.</summary>
public sealed record FindingsReportDocument(IReadOnlyList<FindingsReportItem?>? Findings);

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

        // A finding's same_as names the ledger finding it repeats, at the same place (LOG 511); the request carries the run's ledger.
        var ledger = c.Request.Findings.GroupBy(f => f.FindingId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var subject in c.Present(Types.FindingsReport))
            if (await c.ReadAsync<FindingsReportDocument>(subject) is { } report)
                foreach (var finding in (report.Findings ?? []).Where(f => !string.IsNullOrWhiteSpace(f?.SameAs)))
                {
                    var sameAs = finding!.SameAs!.Trim();
                    if (!ledger.TryGetValue(sameAs, out var named))
                        c.Error("unknown_finding", $"same_as {sameAs} is not a finding in this run's ledger; name a ledger id from findings_ledger, or leave same_as out for a new finding",
                            subject.Name, sameAs);
                    else if (!SamePlace(finding.Location?.Repository, finding.Location?.File, named.Finding.Location))
                        c.Error("same_as_elsewhere", $"same_as {sameAs} names a finding in {named.Finding.Location.Repository} {named.Finding.Location.File}, but this finding is in " +
                            $"{finding.Location?.Repository} {finding.Location?.File}; same_as is only for the same problem at the same place, so leave it out for a finding elsewhere",
                            subject.Name, sameAs);
                }
    }

    /// <summary>A reported repository and file are the ledger finding's: the repository as written or as its full name.</summary>
    internal static bool SamePlace(string? repository, string? file, FindingLocation ledger) =>
        repository is not null && file is not null
        && string.Equals(RepositoryNames.As(repository.Trim(), [ledger.Repository]), ledger.Repository, StringComparison.OrdinalIgnoreCase)
        && string.Equals(file.Trim(), ledger.File, StringComparison.Ordinal);

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
