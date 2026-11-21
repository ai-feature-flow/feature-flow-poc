using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Validation;

namespace FeatureFlow.Interpreter;

/// <summary>
/// What rebuilds one revision of a run's plan with the run's library lock (docs/04 section 7; LOG 404): the run, the
/// template, the revision, the run's repositories and parameters (YAML text, as <see cref="CarriedRunInfo"/>), and the
/// accepted patches before it, each as <see cref="PatchReader.Write"/> writes it. Stored as a <c>plan_revision</c> artifact for
/// each revision.
/// </summary>
public sealed record PlanSource(
    string RunId,
    string Template,
    int Revision,
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> Patches)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored form: JSON, as the engine stores what it shares with the API.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);

    public static PlanSource Read(string text) => JsonSerializer.Deserialize<PlanSource>(text, Json) ?? throw new FormatException("a plan revision is empty");
    /// <summary>The plan of this revision, loaded and validated as the core loads it; null when it does not load.</summary>
    public Plan? Load(LibrarySet libraries, DiagnosticBag diagnostics)
    {
        if (LibraryReference.TryParse(Template) is not { } template)
        {
            diagnostics.Add(Checks.PatchLoadChecks, $"'{Template}' is not a template reference");
            return null;
        }

        var run = new RunInfo("", Repositories, Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Read(kv.Value, $"params.{kv.Key}")));
        return RunCore.Load(libraries, template, run, Patches.Select(PatchReader.ReadWritten).ToList(), diagnostics);
    }
}

public sealed partial class RunCore
{
    int revisionsTaken;

    /// <summary>
    /// The plan revisions accepted since the last take, revision 1 first, drained for the artifact index (LOG 404). The
    /// count taken is carried (<see cref="CarriedCore.RevisionsTaken"/>), so a hand-over takes only revisions not yet taken:
    /// each revision holds every patch before it, and taking them all again would grow the history with each hand-over (LOG 430).
    /// </summary>
    public IReadOnlyList<PlanSource> TakeRevisions()
    {
        if (template is null)
            return [];
        var run = CarriedRunInfo.From(Run);
        var patches = State.Patches.Select(PatchReader.Write).ToList();
        var revisions = Enumerable.Range(revisionsTaken + 1, State.Revision - revisionsTaken)
            .Select(n => new PlanSource(Run.Id, template.ToString(), n, run.Repositories, run.Parameters, patches.Take(n - 1).ToList()))
            .ToList();
        revisionsTaken = State.Revision;
        return revisions;
    }
}
