export interface ArtifactViewerProps {
  runId: string;
  /** The artifact's reference, URL-decoded from #/runs/{id}/artifacts/{ref}. */
  reference: string;
}

/** One artifact or transcript as text (GET /runs/{id}/artifacts/{ref}). A placeholder until task T7. */
export function ArtifactViewer(_: ArtifactViewerProps) {
  return (
    <section class="ff-viewer">
      <h1>Artifact</h1>
      <p>The viewer is not built yet.</p>
    </section>
  );
}
