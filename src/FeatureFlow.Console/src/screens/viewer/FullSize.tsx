import { ArtifactViewer } from "../artifacts/Viewer";

/**
 * An artifact full size, in a popup (app/viewer.tsx). Stub from the console shell (d1): slice 1's viewer, until the
 * workspace package (d2) renders it as its viewer does (A7).
 */
export function FullSize({ runId, reference }: { runId: string; reference: string }) {
  return <ArtifactViewer runId={runId} reference={reference} />;
}
