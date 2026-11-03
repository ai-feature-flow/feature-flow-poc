using System.Text;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.StdValidators.Tests;

/// <summary>
/// The world a standard validator reads: an artifact store, and one repository in an in-memory forge reached through its
/// code host and VCS. Documents are std's shapes as a planner, implementer or fixer writes them.
/// </summary>
public sealed class World : IDisposable
{
    public const string Repo = "acme/api";
    public const string TicketRun = "run-ticket";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-std-validators-" + Guid.NewGuid().ToString("N"));

    public World()
    {
        Directory.CreateDirectory(folder);
        Store = new FileSystemArtifactStore(folder);
        Repository = Forge.CreateRepository(Repo, new Dictionary<string, string>
        {
            ["src/Widgets.cs"] = "class Widgets { }\n",
            ["tests/WidgetTests.cs"] = "class WidgetTests { void Existing() { } }\n",
        });
        Validators = new StdValidators(() => Store, () => new InMemoryCodeHost(Forge), () => new InMemoryVcs(Forge));
    }

    public FileSystemArtifactStore Store { get; }
    public InMemoryForge Forge { get; } = new();
    public RepoRef Repository { get; }
    public StdValidators Validators { get; }

    public string Main => Forge.Resolve(Repository, "main")!;

    public string Commit(string path, string content, string branch = "refs/heads/ff/ticket/T-1") =>
        Forge.Commit(Repository, branch, new Dictionary<string, string?> { [path] = content }, $"change {path}");

    public string Put(string text) => Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None).GetAwaiter().GetResult().Unwrap().Id;

    public async Task<string> ReadAsync(ArtifactRef reference)
    {
        await using var stream = (await Store.GetAsync(reference, CancellationToken.None)).Unwrap();
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
    }

    public string PutEngine(object value) => Put(JsonSerializer.Serialize(value, ArtifactShapes.Engine));

    public ValidationSubject Subject(string name, string type, string text, string from = "current") =>
        new(name, "std:artifact_types/" + type, new ArtifactRef(Put(text)), from);

    /// <summary>A subject of this run and its index entry (an agent's), so a validator can tell which run it belongs to.</summary>
    public (ValidationSubject Subject, ArtifactRecord Record) Indexed(string name, string type, string text, string runId = TicketRun)
    {
        var record = Record(name, type, Put(text), runId: runId, step: name);
        return (new ValidationSubject(name, record.Type, new ArtifactRef(record.Ref)), record);
    }

    public static ValidationSubject Absent(string name, string from = "parent") => new(name, null, null, from);

    /// <summary>An index entry: by default an agent's (no action, not evidence).</summary>
    public static ArtifactRecord Record(string name, string type, string reference, string? action = null, bool evidence = false, string runId = TicketRun,
        string step = "step") => new(runId, step, 1, name, "std:artifact_types/" + type, reference, action, evidence, DateTimeOffset.UnixEpoch);

    /// <summary>Engine test evidence from <c>test.run</c>: one tier at a revision, with the tests that passed and failed.</summary>
    public ArtifactRecord TestEvidence(string revision, IReadOnlyList<string> passing, IReadOnlyList<string>? failing = null, string tier = "unit",
        string runId = TicketRun, string? action = "test.run", bool evidence = true, bool exitCodeOnly = false, bool baseline = false,
        IReadOnlyList<TestResults>? extraTiers = null)
    {
        var failures = (failing ?? []).Select(f => new TestFailure(f, "failed")).ToList();
        var results = new TestResults(revision, tier, passing.Count, failures.Count, 0, failures, exitCodeOnly ? null : passing);
        var reference = PutEngine(new TestEvidence(Repo, revision, baseline, failures.Count == 0, [results, .. extraTiers ?? []]));
        return Record("test_results", "test_results", reference, action, evidence, runId, "tests");
    }

    public static ValidationRequest Request(string validator, IReadOnlyList<ValidationSubject> subjects, IReadOnlyDictionary<string, string>? frozen = null,
        IReadOnlyList<ArtifactRecord>? artifacts = null, IReadOnlyList<FindingRecord>? findings = null, IReadOnlyDictionary<string, string>? options = null) =>
        new(validator, subjects, options ?? new Dictionary<string, string>(), frozen ?? new Dictionary<string, string>(), artifacts ?? [], findings ?? []);

    public static IReadOnlyDictionary<string, string> Frozen(string revision) => new Dictionary<string, string> { [Repo] = revision };

    public static FindingRecord Finding(string id, string? raised, string runId = TicketRun) =>
        new(runId, id, "fp-" + id, new ReportedFinding("R1", "a defect", "it breaks", "high", true, "reasoned", new FindingLocation(Repo, "src/Widgets.cs")),
            "reviewer", null, FindingStates.Fixed, raised, raised, 0, 1, 0, false, DateTimeOffset.UnixEpoch);

    // ---- Documents ----

    public const string EpicRequirements = """
        schema: 1
        scope: { kind: epic, id: E }
        requirements:
          - { id: E-1, title: Widgets are valid, behaviour: An invalid widget is refused., supports_reason: The epic's goal }
        """;

    public const string TicketRequirements = """
        schema: 1
        scope: { kind: ticket, id: T-1 }
        requirements:
          - id: R-1
            title: Names are short
            behaviour: A name over 100 characters is refused.
            supports: [{ artifact: requirements, requirement: E-1 }]
        """;

    public const string Criterion = """
          - id: AC-1
            title: Long names are refused
            behaviour: POST /widgets with a 101-character name returns 400.
            supports: [{ artifact: requirements, requirement: R-1 }]
        """;

    public const string Method = """
            verification:
              - { id: unit, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required }
        """;

    public static string TicketPlan(string criteria = Criterion + "\n" + Method) => "schema: 1\nscope: { kind: ticket, id: T-1 }\nacceptance_criteria:\n" + criteria;

    /// <summary>An epic's plan for its tickets: AC-1 owned by ticket T-1, supporting the epic's E-1.</summary>
    public const string EpicPlan = """
        schema: 1
        scope: { kind: epic, id: E }
        acceptance_criteria:
          - id: AC-1
            title: Long names are refused
            behaviour: POST /widgets with a 101-character name returns 400.
            supports: [{ artifact: requirements, requirement: E-1 }]
            owner: { kind: ticket, id: T-1 }
        """;

    /// <summary>A ticket's plan inheriting AC-1 from <see cref="EpicPlan"/>, with its method added.</summary>
    public static string InheritedPlan(string title = "Long names are refused") => $$"""
        schema: 1
        scope: { kind: ticket, id: T-1 }
        acceptance_criteria:
          - id: AC-1
            title: {{title}}
            behaviour: POST /widgets with a 101-character name returns 400.
            supports: [{ artifact: requirements, requirement: E-1 }]
            verification:
              - { id: unit, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required }
        """;

    public static string Claims(string test = "WidgetTests.RefusesLongNames", string change = "new", string file = "tests/WidgetTests.cs", string criterion = "AC-1") => $$"""
        schema: 1
        claims:
          - criterion: {{criterion}}
            verification: unit
            tests:
              - { repository: acme/api, tier: unit, id: {{test}}, change: {{change}}, source: { file: {{file}} } }
        """;

    public const string TriageText = """
        schema: 1
        entries:
          - { finding: F-1, state: open, reason: valid, regression_test: { applicable: true } }
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
