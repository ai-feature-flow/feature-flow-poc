namespace FeatureFlow.Model;

/// <summary>A position in a source file. Lines and columns start at 1.</summary>
public sealed record SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

public enum Severity
{
    Error,
    Warning,
}

/// <summary>
/// One finding of a check of docs/09 section 2. <see cref="Check"/> is the stable check number.
/// </summary>
public sealed record Diagnostic(int Check, Severity Severity, string Message, SourceLocation? Location = null, string? StepId = null)
{
    public bool Blocks => Severity == Severity.Error;

    public override string ToString()
    {
        var where = Location is null ? "" : $"{Location}: ";
        var step = StepId is null ? "" : $"[{StepId}] ";
        var kind = Blocks ? "error" : "warning";
        return $"{where}{kind} check {Check} ({Checks.Title(Check)}): {step}{Message}";
    }
}

/// <summary>The check numbers and titles of docs/09 section 2.</summary>
public static class Checks
{
    public const int Schema = 1;
    public const int UnknownAction = 2;
    public const int DuplicateId = 3;
    public const int UndeclaredParameter = 4;
    public const int Expression = 5;
    public const int Outcomes = 6;
    public const int OutcomeRouted = 7;
    public const int RouteTarget = 8;
    public const int Reachable = 9;
    public const int CycleExit = 10;
    public const int ArtifactProducer = 11;
    public const int EvidenceType = 12;
    public const int WriteAccess = 13;
    public const int Fragment = 14;
    public const int ParallelRoutes = 15;
    public const int DecisionType = 16;
    public const int PlanSize = 17;
    public const int RequiredParameters = 18;
    public const int ReferenceResolves = 19;
    public const int RepositoryLibraryAddsOnly = 20;
    public const int Capabilities = 21;
    public const int LockedCap = 22;
    public const int PatchBaseRevision = 25;
    public const int PatchResumeAt = 26;
    public const int PatchProposer = 27;
    public const int PatchLoadChecks = 28;
    public const int PatchProtected = 29;
    public const int PatchFloor = 30;
    public const int UnusedParameter = 34;
    public const int DeprecatedReference = 35;

    static readonly Dictionary<int, string> Titles = new()
    {
        [1] = "schema conformance",
        [2] = "unknown action or construct",
        [3] = "duplicate step id",
        [4] = "undeclared parameter",
        [5] = "expression",
        [6] = "declared outcomes",
        [7] = "every outcome routed",
        [8] = "route target",
        [9] = "reachability",
        [10] = "cycle without a route out",
        [11] = "consumed artifact producer",
        [12] = "evidence type produced by an agent or decision",
        [13] = "write access and tools",
        [14] = "fragment",
        [15] = "parallel branch routes",
        [16] = "decision type declared",
        [17] = "plan size",
        [18] = "required parameters bound",
        [19] = "library reference resolves",
        [20] = "repository library section adds only",
        [21] = "capability negotiation",
        [22] = "organisation cap",
        [25] = "patch base revision current",
        [26] = "patch resume_at exists",
        [27] = "patch proposer may use the operation",
        [28] = "patch result passes the load checks",
        [29] = "patch touches protected content or an unlisted role",
        [30] = "patch relaxes a safety floor",
        [34] = "unused parameter",
        [35] = "deprecated library reference",
    };

    public static string Title(int check) => Titles.TryGetValue(check, out var title) ? title : "check";

    /// <summary>Checks 34 and 35 warn; every other check blocks (docs/09 section 1).</summary>
    public static Severity SeverityOf(int check) => check is UnusedParameter or DeprecatedReference ? Severity.Warning : Severity.Error;
}

/// <summary>An append-only list of diagnostics, with the severity taken from the check number.</summary>
public sealed class DiagnosticBag : IReadOnlyList<Diagnostic>
{
    readonly List<Diagnostic> items = [];

    public void Add(int check, string message, SourceLocation? location = null, string? stepId = null) =>
        items.Add(new Diagnostic(check, Checks.SeverityOf(check), message, location, stepId));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => items.AddRange(diagnostics);

    public bool HasErrors => items.Any(d => d.Blocks);

    public Diagnostic this[int index] => items[index];
    public int Count => items.Count;
    public IEnumerator<Diagnostic> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
