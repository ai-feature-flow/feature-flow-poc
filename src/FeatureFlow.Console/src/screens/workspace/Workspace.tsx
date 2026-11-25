import type { ComponentChildren, RefObject } from "preact";
import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import { api, read } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { setPref, usePref } from "../../app/prefs";
import type { Selection } from "../../app/router";
import { ViewerContext } from "../../app/viewer";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Splitter, useCssVar } from "../../ui/Splitter";
import { useDecision } from "../decision/DecisionPopup";
import { useTabs, ViewerPane } from "../viewer/ViewerPane";
import { GroupPage } from "./root/GroupPage";
import { RunPage } from "./root/RunPage";
import { StepDetail } from "./step/StepDetail";
import { build, changed, flatten, type Node, runOf, runRow, statuses, stepRow } from "./tree/model";
import { Tree } from "./tree/Tree";
import "./Workspace.css";

/** Below this width the right pane closes and the tree becomes a drawer (design-v2 s5). */
const narrowQuery = "(max-width: 999px)";

function useNarrow(): boolean {
  const [narrow, setNarrow] = useState(() => matchMedia(narrowQuery).matches);
  useEffect(() => {
    const query = matchMedia(narrowQuery);
    const changed = () => setNarrow(query.matches);
    query.addEventListener("change", changed);
    return () => query.removeEventListener("change", changed);
  }, []);
  return narrow;
}

/** The address of a row: a step's route, else its run's (a child run's own route opens it in its root's tree); with `own`, in the root's own tree. */
function linkOf(rootId: string, node: Node, own: boolean): string {
  if (node.kind === "step") return links.step(rootId, node.runId, node.step.id, own);
  const runId = runOf(node).runId;
  return runId === rootId ? links.tree(rootId, own) : links.treeRun(rootId, runId, own);
}

/** The row a route names, once the tree is read: a run, a step (else its run), a wait's step (else its run), an artifact's run. */
function rowOf(rows: readonly Node[], select: Selection): string {
  switch (select.kind) {
    case "step":
      return rows.some(r => r.key === stepRow(select.runId, select.stepId)) ? stepRow(select.runId, select.stepId) : runRow(select.runId);
    case "wait": {
      const run = rows.find(r => r.kind === "run" && r.runId === select.runId);
      const wait = run?.kind === "run" ? run.waits.find(w => w.wait_id === select.waitId) : undefined;
      return wait ? stepRow(select.runId, wait.step_id) : runRow(select.runId);
    }
    default:
      return runRow(select.runId);
  }
}

/**
 * The run workspace (design-v2 s4.4; plan s5.2, s5.3): the whole run tree on the left, the selected row in the middle and the
 * viewer on the right. `tree` may name any run of the tree: GET /runs/{id}/tree answers its root's tree, and the route's run,
 * step, wait (its popup opened) or artifact (in the viewer) is selected. A selection made here goes to the address bar
 * without a navigation, so a reload keeps it and an open popup stays open. A frame naming any run of the tree reads it again.
 * With `own`, the run named is the root shown, whatever runs above it (the tree's ↗).
 */
export function Workspace({ tree, own, select }: { tree: string; own: boolean; select: Selection }) {
  const [runIds, setRunIds] = useState<readonly string[]>([tree]);
  const live = useLive(runIds.map(runKey), () => read(api.GET("/runs/{id}/tree", { params: { path: { id: tree } } })));
  const root = useMemo(() => (live.data ? build(live.data, own ? tree : undefined) : undefined), [live.data]);
  const rows = useMemo(() => (root ? flatten(root) : []), [root]);
  useEffect(() => {
    const ids = rows.filter(r => r.kind === "run").map(r => r.runId);
    if (ids.length > 0 && ids.join("\n") !== runIds.join("\n")) setRunIds(ids);
  }, [rows]);

  const [selected, setSelected] = useState<string>();
  const [follow, setFollow] = useState(true);
  const [drawer, setDrawer] = useState(false);
  const narrow = useNarrow();
  const tabs = useTabs();
  const decisions = useDecision();
  const viewer = useMemo(() => ({ openArtifact: tabs.openArtifact }), [tabs.openArtifact]);

  const choose = (node: Node) => {
    setSelected(node.key);
    setDrawer(false);
    if (root) history.replaceState(history.state, "", linkOf(root.runId, node, own));
  };

  // The route's selection, once the tree is read and on every route change after.
  const routed = JSON.stringify(select);
  const read_ = root !== undefined;
  useEffect(() => {
    if (!read_) return;
    setSelected(rowOf(rows, select));
    if (select.kind === "wait") {
      const run = rows.find(r => r.kind === "run" && r.runId === select.runId);
      const wait = run?.kind === "run" ? run.waits.find(w => w.wait_id === select.waitId) : undefined;
      if (wait) decisions.open(select.runId, wait, root?.runId);
    }
    if (select.kind === "artifact") tabs.openArtifact({ runId: select.runId, reference: select.reference });
  }, [routed, read_]);

  // Follow current: a step whose status a later read changed takes the selection (design-v2 Q23); the last to change, if several.
  const seen = useRef<Map<string, string> | undefined>(undefined);
  useEffect(() => {
    if (!read_) return;
    const moved = seen.current && changed(seen.current, rows);
    if (moved && follow) choose(moved);
    seen.current = statuses(rows);
  }, [rows]);

  if (!root) return <div class="ff-page">{live.error ? <ErrorBox error={live.error} /> : <Busy />}</div>;
  const current = rows.find(r => r.key === selected) ?? root;

  return (
    <ViewerContext.Provider value={viewer}>
      <Panes
        narrow={narrow}
        drawer={drawer}
        left={<Tree root={root} rows={rows} selected={current.key} onSelect={choose} follow={follow} onFollow={setFollow} />}
        middle={
          <>
            {narrow && (
              <button type="button" class="ff-btn ff-btn-ghost ff-drawer-toggle" aria-expanded={drawer} onClick={() => setDrawer(d => !d)}>
                Tree
              </button>
            )}
            {live.error && <ErrorBox error={live.error} />}
            {current.kind === "step" ? (
              <StepDetail key={current.key} node={current} />
            ) : current.kind === "group" ? (
              <GroupPage node={current} />
            ) : (
              <RunPage key={runOf(current).runId} node={runOf(current)} rows={rows} onSelect={choose} />
            )}
          </>
        }
        right={<ViewerPane tabs={tabs} />}
      />
    </ViewerContext.Provider>
  );
}

/**
 * The three panes: the side panes' widths dragged at their inner borders and remembered, the right one collapsible (from the
 * top bar or its own icon). In a narrow window the right pane is closed and the tree is a drawer over the middle.
 */
function Panes({ narrow, drawer, left, middle, right }: {
  narrow: boolean;
  drawer: boolean;
  left: ComponentChildren;
  middle: ComponentChildren;
  right: ComponentChildren;
}) {
  const leftWidth = usePref("leftWidth");
  const rightWidth = usePref("rightWidth");
  const collapsed = usePref("rightCollapsed");
  const shell = useRef<HTMLDivElement>(null);
  useCssVar(shell as RefObject<HTMLElement>, "--left", leftWidth);
  useCssVar(shell as RefObject<HTMLElement>, "--right", rightWidth);
  const withRight = !narrow && !collapsed;
  const classes = ["ff-shell", withRight ? "ff-shell-right" : "ff-shell-noright"];
  if (narrow) classes.push("ff-shell-narrow");
  if (narrow && drawer) classes.push("ff-shell-drawer");

  return (
    <div ref={shell} class={classes.join(" ")}>
      <aside class="ff-left" aria-label="Tree">
        {left}
      </aside>
      {!narrow && <Splitter label="Resize the tree" pane="left" value={leftWidth} min={240} max={720} onChange={w => setPref("leftWidth", w)} />}
      <section class="ff-middle" aria-label="Selected">
        {middle}
      </section>
      {withRight && <Splitter label="Resize the viewer" pane="right" value={rightWidth} min={260} max={900} onChange={w => setPref("rightWidth", w)} />}
      {withRight && (
        <aside class="ff-right" aria-label="Viewer">
          {right}
        </aside>
      )}
    </div>
  );
}
