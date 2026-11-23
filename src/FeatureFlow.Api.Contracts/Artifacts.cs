namespace FeatureFlow.Api.Contracts;

/// <summary>What an indexed artifact is: a declared artifact, evidence, an agent session's transcript or prompt, or a revision of the run's plan.</summary>
public enum ArtifactKind
{
    Artifact,
    Evidence,
    Transcript,
    Prompt,
    PlanRevision,
}

/// <summary><c>GET /runs/{id}/artifacts</c>: the run's own artifact index, in the order recorded.</summary>
public sealed record ArtifactList(IReadOnlyList<ArtifactView> Artifacts);

/// <summary>
/// An indexed artifact: the step and execution that produced it, its name and declared type, its reference (read with
/// <c>GET /runs/{id}/artifacts/{reference}</c>), the engine action that made it, and whether it is evidence.
/// </summary>
public sealed record ArtifactView(
    string StepId,
    int Execution,
    string Name,
    string? Type,
    string Ref,
    string? Action,
    bool Evidence,
    ArtifactKind Kind,
    DateTimeOffset At);

/// <summary>
/// <c>GET /runs/{id}/artifacts/{reference}/document</c>: an artifact read as a document: its format (<c>yaml</c>, <c>json</c>,
/// <c>markdown</c>, <c>text</c>), its data as JSON text (YAML or JSON parsed and written as JSON, or a Markdown document's
/// front matter; null for text), and its body as text (Markdown after the front matter, or plain text). The data is text, not
/// a JSON value, so every schema in the document stays typed.
/// </summary>
public sealed record ArtifactDocument(string Format, string? Data, string? Body);
