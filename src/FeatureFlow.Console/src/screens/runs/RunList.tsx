import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { useLive } from "../../api/live";
import * as links from "../../app/links";
import { useSession } from "../../auth/session";
import { Badge } from "../../ui/Badge";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Pill } from "../../ui/Pill";
import { runLabel, RunName, Segmented, stepPath, took, when } from "./parts";
import "./parts.css";
import "./Runs.css";

type Run = Schema<"RunSummary">;
type Who = "mine" | "everyone";
type When = "active" | "finished" | "all";

/** The statuses a run ends with (Interpreter RunStatus); any other is active. */
const finished = new Set(["completed", "failed", "rejected", "aborted", "partial", "incomplete"]);
const isFinished = (run: Run): boolean => finished.has(run.status);

/** A run tree: its root, and every run under it, depth first in the API's order. */
interface Tree {
  root: Run;
  runs: Run[];
}

/** The runs as trees: a run whose parent the caller cannot see is a root too. */
function treesOf(runs: readonly Run[]): Tree[] {
  const byId = new Map(runs.map(r => [r.run_id, r]));
  const children = new Map<string, Run[]>();
  for (const run of runs)
    if (run.parent_run_id !== null && byId.has(run.parent_run_id)) children.set(run.parent_run_id, [...(children.get(run.parent_run_id) ?? []), run]);
  const under = (run: Run): Run[] => (children.get(run.run_id) ?? []).flatMap(c => [c, ...under(c)]);
  return runs.filter(r => r.parent_run_id === null || !byId.has(r.parent_run_id)).map(root => ({ root, runs: under(root) }));
}

/** Whether the run is found by the search: its ticket, title, repositories or id contain the text, in any case. */
function matches(run: Run, text: string): boolean {
  const fields = [run.run_id, run.tracker_issue?.key, run.tracker_issue?.title, run.title, ...run.repositories];
  return fields.some(f => f?.toLowerCase().includes(text));
}

/** "2 epics, 4 tickets so far" for a tree with child runs, by their level. */
function descendants(tree: Tree): string | null {
  if (tree.runs.length === 0) return null;
  const counts = new Map<string, number>();
  for (const run of tree.runs) counts.set(run.level ?? "child run", (counts.get(run.level ?? "child run") ?? 0) + 1);
  return `${[...counts].map(([level, n]) => `${n} ${level}${n === 1 ? "" : "s"}`).join(", ")} so far`;
}

/** Where the tree is now: each current step, in a child run named by that run. */
function Now({ run, byId }: { run: Run; byId: Map<string, Run> }) {
  const steps = run.current_steps ?? [];
  if (steps.length === 0) return <>–</>;
  return (
    <>
      {steps.slice(0, 3).map(s => {
        const child = s.run_id === run.run_id ? undefined : byId.get(s.run_id);
        return (
          <div key={`${s.run_id}\n${s.step_id}`}>
            {child ? `${runLabel(child)} › ` : s.run_id === run.run_id ? "" : `${s.run_id} › `}
            {stepPath(s.step_id)}
          </div>
        );
      })}
      {steps.length > 3 && <div class="ff-muted">and {steps.length - 3} more</div>}
    </>
  );
}

function StatusPill({ run }: { run: Run }) {
  if ((run.needs_you ?? 0) > 0) return <Pill tone="wait">needs you</Pill>;
  return <Pill tone={run.status === "completed" ? "ok" : "neutral"}>{run.status}</Pill>;
}

const count = (n: number | undefined, tone: "need" | "run") => (n ? <Badge tone={tone}>{n}</Badge> : "–");

/** A run's row (a root's opens its tree; a child's opens its root's tree with it selected): its name over its id, pull requests and children; for an active run, the counts and where it is now. */
function Row({ run, tree, depth, byId, active }: { run: Run; tree?: Tree; depth: number; byId: Map<string, Run>; active: boolean }) {
  const label = runLabel(run);
  const sub = [
    label === run.run_id ? null : run.run_id,
    run.level ? null : run.template, ...(run.pull_requests ?? []).map(pr => `PR #${pr.number} ${pr.state}`), tree ? descendants(tree) : null];
  const repos = run.repositories.length <= 1 ? run.repositories[0] ?? "–" : `${run.repositories.length} repositories`;
  const end = isFinished(run) ? run.updated_at : new Date().toISOString();

  return (
    <tr
      data-run={run.run_id}
      class={depth > 0 ? "ff-runs-child" : undefined}
      onClick={e => {
        if (!(e.target instanceof Element && e.target.closest("a"))) location.hash = links.run(run.run_id);
      }}
    >
      <td>
        <div class="ff-runs-name">
          {depth > 0 && <span class="ff-muted">↳ </span>}
          <a href={links.run(run.run_id)}>
            <RunName run={run} strong />
          </a>
        </div>
        <div class="ff-runs-sub ff-muted">{sub.filter(s => s !== null).join(" · ")}</div>
      </td>
      <td>
        <StatusPill run={run} />
      </td>
      {active && <td class="ff-num">{count(run.needs_you, "need")}</td>}
      {active && <td class="ff-num">{count(run.running, "run")}</td>}
      {active && (
        <td>
          <Now run={run} byId={byId} />
        </td>
      )}
      <td>{run.started_at ? when(run.started_at) : "–"}</td>
      <td>{run.started_at ? took(run.started_at, end) : "–"}</td>
      <td title={run.repositories.join(", ")}>{repos}</td>
    </tr>
  );
}

function TreeTable({ trees, active, withChildren, byId }: { trees: Tree[]; active: boolean; withChildren: boolean; byId: Map<string, Run> }) {
  const head = active
    ? ["Run tree", "Status", "Needs you", "Running", "Now", "Started", "Took", "Repository"]
    : ["Run tree", "Status", "Started", "Took", "Repository"];
  return (
    <div class="ff-box">
      <table class="ff-runs" aria-label={active ? "Active runs" : "Finished runs"}>
        <thead>
          <tr>
            {head.map(h => (
              <th key={h} class={h === "Needs you" || h === "Running" ? "ff-num" : undefined}>
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {trees.flatMap(tree => [
            <Row key={tree.root.run_id} run={tree.root} tree={tree} depth={0} byId={byId} active={active} />,
            ...(withChildren ? tree.runs.map(run => <Row key={run.run_id} run={run} depth={1} byId={byId} active={active} />) : []),
          ])}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Runs (#/runs; design-v2 s4.3, mock-up 06): a row per run tree (a top-level run), its children inside it, with search,
 * Mine / Everyone, Active / Finished / All and "list child runs too", all over GET /runs. A row opens the tree's workspace.
 */
export function RunList() {
  const runs = useLive(["runs"], () => read(api.GET("/runs")));
  const session = useSession();
  const [search, setSearch] = useState("");
  const [who, setWho] = useState<Who>("mine");
  const [shown, setShown] = useState<When>("active");
  const [withChildren, setWithChildren] = useState(false);
  if (runs.data === undefined) return runs.error ? <ErrorBox error={runs.error} /> : <Busy />;

  const actor = session.state === "signed-in" ? session.me.actor : null;
  const text = search.trim().toLowerCase();
  const byId = new Map(runs.data.runs.map(r => [r.run_id, r]));
  const trees = treesOf(runs.data.runs).filter(
    t => (who === "everyone" || t.root.requester === actor) && (text === "" || [t.root, ...t.runs].some(r => matches(r, text))),
  );
  const active = trees.filter(t => !isFinished(t.root));
  const ended = trees.filter(t => isFinished(t.root));

  return (
    <section class="ff-list-page ff-runs-page">
      <h1>Runs</h1>
      <p class="ff-sub">Run trees: each row is a top-level run; its epics and tickets are inside it.</p>
      {runs.error && <ErrorBox error={runs.error} />}
      <div class="ff-toolbar">
        <input
          type="search"
          class="ff-search"
          aria-label="Search runs"
          placeholder="Search ticket, title, repository, run id"
          value={search}
          onInput={e => setSearch(e.currentTarget.value)}
        />
        <Segmented<Who> label="Whose" value={who} options={[["mine", "Mine"], ["everyone", "Everyone"]]} onChange={setWho} />
        <Segmented<When> label="State" value={shown} options={[["active", "Active"], ["finished", "Finished"], ["all", "All"]]} onChange={setShown} />
        <label class="ff-muted ff-runs-toggle">
          <input type="checkbox" checked={withChildren} onChange={e => setWithChildren(e.currentTarget.checked)} /> list child runs too
        </label>
        <a class="ff-btn ff-btn-primary ff-toolbar-end" href={links.start()}>
          Start a run
        </a>
      </div>
      {shown !== "finished" &&
        (active.length > 0 ? <TreeTable trees={active} active withChildren={withChildren} byId={byId} /> : <p>No active runs{text ? " match" : ""}.</p>)}
      {shown !== "active" && (
        <>
          {shown === "all" && <h2>Finished</h2>}
          {ended.length > 0 ? (
            <TreeTable trees={ended} active={false} withChildren={withChildren} byId={byId} />
          ) : (
            <p>No finished runs{text ? " match" : ""}.</p>
          )}
        </>
      )}
    </section>
  );
}
