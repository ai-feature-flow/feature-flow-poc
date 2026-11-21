export interface ArtifactListProps {
  runId: string;
}

/** The run's artifacts, evidence, transcripts, prompts and plan revisions (GET /runs/{id}/artifacts). A placeholder until task T7. */
export function ArtifactList(_: ArtifactListProps) {
  return (
    <section class="ff-artifacts">
      <h2>Artifacts</h2>
      <p>The artifact list is not built yet.</p>
    </section>
  );
}
