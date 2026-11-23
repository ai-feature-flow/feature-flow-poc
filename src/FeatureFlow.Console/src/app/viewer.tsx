import { type ComponentChildren, createContext } from "preact";
import { useContext } from "preact/hooks";
import { FullSize } from "../screens/viewer/FullSize";
import { type Popups, usePopup } from "./popup";

// Opening an artifact (design-v2 s4.4, plan s5.4): the workspace provides a viewer (its right pane, a preview tab the next
// file replaces); anywhere else, such as Needs me, an artifact opens full size in a popup. Every link to one is an
// ArtifactLink: its name opens it there, the pop-out icon after it full size.

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

/** Opens the artifact full size, in a popup. */
export const openFullSize = (popups: Popups, target: ArtifactTarget) =>
  popups.open(<FullSize runId={target.runId} reference={target.reference} />, { label: target.name ?? target.reference, wide: true });

/** The workspace's viewer, or one that opens every artifact full size in a popup. */
export function useViewer(): Viewer {
  const viewer = useContext(ViewerContext);
  const popups = usePopup();
  return viewer ?? { openArtifact: target => openFullSize(popups, target) };
}

/**
 * A link to an artifact, wherever one is shown: its text (the artifact's name unless given) opens it in the viewer; the
 * pop-out icon after it opens it full size in a popup straight away.
 */
export function ArtifactLink({ target, children }: { target: ArtifactTarget; children?: ComponentChildren }) {
  const viewer = useViewer();
  const name = target.name ?? target.reference;
  return (
    <span class="ff-artlink">
      <button type="button" class="ff-linkbtn" onClick={() => viewer.openArtifact(target)}>
        {children ?? name}
      </button>
      <button type="button" class="ff-popout" aria-label={`Open ${name} full size`} title="Open full size, in a popup" onClick={() => viewer.openArtifact(target, { full: true })}>
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M14 4h6v6" />
          <path d="M20 4l-9 9" />
          <path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
        </svg>
      </button>
    </span>
  );
}
