import { Document } from "./Document";
import "./viewer.css";

/** An artifact full size, in a popup (app/viewer.tsx): from a viewer tab (double-click or ⤢), or from outside the workspace. */
export function FullSize({ runId, reference }: { runId: string; reference: string }) {
  return (
    <div class="ff-fullsize">
      <Document runId={runId} reference={reference} />
    </div>
  );
}
