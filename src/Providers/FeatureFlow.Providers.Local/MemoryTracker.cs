using System.Collections.Concurrent;
using System.Globalization;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A tracker in memory (docs/06 section 3.6), for engine tests: issues keyed <c>MEM-n</c>, created in status <c>to_do</c>,
/// moved only to the library statuses it maps (by default std's <c>in_progress</c>, <c>code_review</c> and <c>done</c>).
/// Providers configured with the same <c>world</c> share one tracker. <see cref="Fail"/> makes the next calls of an
/// operation fail, as the forge's faults do.
/// </summary>
public sealed class InMemoryTracker(IReadOnlyCollection<string>? statuses = null) : ITrackerProvider
{
    static readonly ConcurrentDictionary<string, InMemoryTracker> Worlds = new(StringComparer.Ordinal);

    public static InMemoryTracker Named(string name) => Worlds.GetOrAdd(name, _ => new InMemoryTracker());

    /// <summary>The status a created issue starts in: the tracker's own, mapped to no library status.</summary>
    public const string Initial = "to_do";

    sealed class Stored
    {
        public required string Key;
        public required string Marker;
        public required string Project;
        public string Title = "";
        public string Body = "";
        public string Status = Initial;
        public List<string> Labels = [];
        public IssueRef? Parent;
        public Dictionary<string, string> Fields = new(StringComparer.Ordinal);
        public readonly List<(string Key, string Body)> Comments = [];
        public readonly HashSet<(string To, string Type)> Links = [];
    }

    readonly IReadOnlySet<string> mapped = new HashSet<string>(statuses ?? ["in_progress", "code_review", "done"], StringComparer.Ordinal);
    readonly Lock gate = new();
    readonly List<Stored> issues = [];
    readonly Dictionary<string, Queue<ProviderError>> faults = new(StringComparer.Ordinal);

    public ProviderDescription Describe() => new("tracker", "memory", "0.1.0", new TrackerCapabilities(true, true, true, true, true, true, true));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    /// <summary>The next <paramref name="times"/> calls of <paramref name="operation"/> (a method name without <c>Async</c>, such as <c>Transition</c>) fail with <paramref name="error"/>.</summary>
    public void Fail(string operation, ProviderError error, int times = 1)
    {
        lock (gate)
        {
            if (!faults.TryGetValue(operation, out var queue))
                faults[operation] = queue = new Queue<ProviderError>();
            for (var i = 0; i < times; i++)
                queue.Enqueue(error);
        }
    }

    /// <summary>An issue's comment bodies, in order.</summary>
    public IReadOnlyList<string> Comments(IssueRef issue)
    {
        lock (gate)
            return Find(issue)?.Comments.Select(c => c.Body).ToList() ?? [];
    }

    /// <summary>An issue's links, as (to, type).</summary>
    public IReadOnlyList<(string To, string Type)> Links(IssueRef issue)
    {
        lock (gate)
            return Find(issue)?.Links.ToList() ?? [];
    }

    public Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken) =>
        Run("GetIssue", () => Find(issue) is { } found ? ProviderResult<Issue>.Success(View(found)) : NotFound<Issue>(issue));

    public Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken) =>
        Run("Search", () => ProviderResult<IReadOnlyList<Issue>>.Success(issues
            .Where(i => (query.Label is null || i.Labels.Contains(query.Label)) && (query.Parent is null || i.Parent?.Key == query.Parent.Key)
                        && (query.Marker is null || i.Marker == query.Marker) && (query.Project is null || i.Project == query.Project))
            .Select(View).ToList()));

    public Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken) =>
        Run("Create", () =>
        {
            if (issues.FirstOrDefault(i => i.Marker == key) is { } existing)
                return ProviderResult<IssueRef>.Success(new IssueRef(existing.Key));
            if (string.IsNullOrWhiteSpace(spec.Title))
                return ProviderResult<IssueRef>.Failure("validation_failed", "an issue needs a title");
            if (spec.Parent is { } parent && Find(parent) is null)
                return NotFound<IssueRef>(parent);
            var stored = new Stored { Key = "MEM-" + (issues.Count + 1).ToString(CultureInfo.InvariantCulture), Marker = key, Project = spec.Project ?? "MEM" };
            Apply(stored, spec);
            issues.Add(stored);
            return ProviderResult<IssueRef>.Success(new IssueRef(stored.Key));
        });

    public Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken) =>
        Run("Update", () =>
        {
            if (Find(issue) is not { } stored)
                return NotFound<IssueRef>(issue);
            Apply(stored, spec);
            return ProviderResult<IssueRef>.Success(issue);
        });

    public Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken) =>
        Run("Transition", () =>
        {
            if (Find(issue) is not { } stored)
                return NotFound<Unit>(issue);
            if (!mapped.Contains(status))
                return ProviderResult<Unit>.Failure("validation_failed", $"status '{status}' is not mapped to a tracker status ({string.Join(", ", mapped.Order())})");
            stored.Status = status;
            return ProviderResult<Unit>.Success(default);
        });

    public Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken) =>
        Run("Link", () =>
        {
            if (Find(from) is not { } stored)
                return NotFound<Unit>(from);
            if (Find(to) is null)
                return NotFound<Unit>(to);
            stored.Links.Add((to.Key, linkType));
            return ProviderResult<Unit>.Success(default);
        });

    public Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken) =>
        Run("Comment", () =>
        {
            if (Find(issue) is not { } stored)
                return NotFound<CommentRef>(issue);
            var at = stored.Comments.FindIndex(c => c.Key == key);
            if (at < 0)
            {
                stored.Comments.Add((key, body));
                at = stored.Comments.Count - 1;
            }

            return ProviderResult<CommentRef>.Success(new CommentRef($"{stored.Key}-{(at + 1).ToString(CultureInfo.InvariantCulture)}"));
        });

    static void Apply(Stored stored, IssueSpec spec)
    {
        stored.Title = spec.Title ?? stored.Title;
        stored.Body = spec.Body ?? stored.Body;
        stored.Labels = spec.Labels?.ToList() ?? stored.Labels;
        stored.Parent = spec.Parent ?? stored.Parent;
        foreach (var (name, value) in spec.Fields ?? new Dictionary<string, string>())
            stored.Fields[name] = value;
    }

    static Issue View(Stored s) =>
        new(new IssueRef(s.Key), s.Title, s.Body, s.Status, s.Labels.ToList(), s.Parent, new Dictionary<string, string>(s.Fields, StringComparer.Ordinal), $"memory://tracker/{s.Key}");

    Stored? Find(IssueRef issue) => issues.FirstOrDefault(i => i.Key == issue.Key);

    static ProviderResult<T> NotFound<T>(IssueRef issue) => ProviderResult<T>.Failure("not_found", $"no issue {issue.Key}");

    Task<ProviderResult<T>> Run<T>(string operation, Func<ProviderResult<T>> work)
    {
        lock (gate)
        {
            if (faults.TryGetValue(operation, out var queue) && queue.Count > 0)
                return Task.FromResult(new ProviderResult<T>(default, queue.Dequeue()));
            return Task.FromResult(work());
        }
    }
}
