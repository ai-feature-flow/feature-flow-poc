import { useEffect } from "preact/hooks";
import { api, read } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { usePopup } from "../../app/popup";
import type { Selection } from "../../app/router";
import { ArtifactViewer } from "../artifacts/Viewer";
import { DecisionPopup } from "../decision/DecisionPopup";
import { RunPage } from "../run/RunPage";

/**
 * The run workspace (#/runs/{tree}/…, plan s5.2): the tree, the selected node and the viewer. Stub from the console
 * shell (d1) so every route resolves: slice 1's run page for the selected run (a wait's route also opens its decision
 * popup), or its artifact viewer, until the workspace package (d2) builds the three panes.
 */
export function Workspace({ tree, select }: { tree: string; select: Selection }) {
  return (
    <div class="ff-page">
      {select.kind === "artifact" ? (
        <ArtifactViewer key={`${select.runId}\n${select.reference}`} runId={select.runId} reference={select.reference} />
      ) : (
        <RunPage key={`${tree}\n${select.runId}`} runId={select.runId} />
      )}
      {select.kind === "wait" && <OpenWait key={`${select.runId}\n${select.waitId}`} runId={select.runId} waitId={select.waitId} />}
    </div>
  );
}

/** Opens the wait's decision popup once its run has been read, if the wait is open. */
function OpenWait({ runId, waitId }: { runId: string; waitId: string }) {
  const detail = useLive([runKey(runId)], () => read(api.GET("/runs/{id}", { params: { path: { id: runId } } })));
  const popups = usePopup();
  const wait = detail.data?.waits.find(w => w.wait_id === waitId && w.open);
  const found = wait !== undefined;
  useEffect(() => {
    if (wait) popups.open(<DecisionPopup runId={runId} wait={wait} />, { label: `${wait.kind} at ${wait.step_id}` });
  }, [found]);
  return null;
}
