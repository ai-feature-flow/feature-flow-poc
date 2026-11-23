import { createContext } from "preact";
import { useContext } from "preact/hooks";
import { FullSize } from "../screens/viewer/FullSize";
import { usePopup } from "./popup";

// Opening an artifact (design-v2 s4.4, plan s5.4): the workspace provides a viewer (its right pane, a preview tab the next
// file replaces); anywhere else, such as Needs me, an artifact opens full size in a popup.

export interface ArtifactTarget {
  runId: string;
  /** The artifact's reference (its content id). */
  reference: string;
  /** Its name, for the tab or the popup's title; the reference when not given. */
  name?: string;
}

export interface Viewer {
  /** Shows the artifact: in the viewer as its preview tab, pinned when `pin`, or full size when `full`. */
  openArtifact(target: ArtifactTarget, how?: { pin?: boolean; full?: boolean }): void;
}

/** The viewer of the screen around; null outside the workspace. */
export const ViewerContext = createContext<Viewer | null>(null);

/** The workspace's viewer, or one that opens every artifact full size in a popup. */
export function useViewer(): Viewer {
  const viewer = useContext(ViewerContext);
  const popups = usePopup();
  return (
    viewer ?? {
      openArtifact: target => popups.open(<FullSize runId={target.runId} reference={target.reference} />, { label: target.name ?? target.reference, wide: true }),
    }
  );
}
