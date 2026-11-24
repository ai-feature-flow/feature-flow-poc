import { api, read, type Schema } from "../../../api/client";
import { runKey, useLive } from "../../../api/live";
import * as links from "../../../app/links";
import { Age } from "../../../ui/Age";
import { ErrorBox } from "../../../ui/ErrorBox";
import { useDecision } from "../../decision/DecisionPopup";
import { took } from "../../runs/parts";
import { ancestors, descendants, isActive, navigable, type Node, pathOf, type RunNode, type State, type StepNode } from "../tree/model";
import { RunActions } from "./RunActions";

const levels: readonly string[] = ["project", "epic", "ticket"];

const stateWords: Record<State, string> = {
  done: "done",
  running: "running",
  needs: "needs you",
  waits: "waiting",
  problem: "in trouble",
  pending: "not started",
  ghost: "not planned yet",
};

/** A run's own rows: those under it but not inside a child run. */
function own(run: RunNode): Node[] {
  const list: Node[] = [];
  const walk = (n: Node) => {
    for (const c of n.children) {
      if (c.kind === "run") continue;
      list.push(c);
      walk(c);
    }
  };
  walk(run);
  return list;
}

/** The run a child run sits in. */
const runParent = (node: Node): RunNode | undefined => ancestors(node).reverse().find((a): a is RunNode => a.kind === "run");

/** Where a run is: the step that needs you or runs (its group, then its name), else done or waiting. */
function stageOf(run: RunNode): string {
  const steps = own(run).filter((n): n is StepNode => n.kind === "step");
  const here = steps.find(s => s.state === "needs" || s.state === "problem") ?? steps.find(s => s.state === "running" || s.state === "waits");
  if (here) {
    const top = [here.parent, here].filter((n): n is Node => n !== null && n.kind !== "run");
    return top.map(n => n.label).join(" › ");
  }
  return run.state === "done" ? "done" : "waiting";
}

/** A run's progress bar: the share of its own steps done, running, and needing you or in trouble. */
function Bar({ run }: { run: RunNode }) {
  const steps = own(run).filter(n => n.kind === "step");
  const share = (states: readonly State[]) => `${(100 * steps.filter(s => states.includes(s.state)).length) / Math.max(1, steps.length)}%`;
  return (
    <div class="ff-bar" role="img" aria-label={`${steps.filter(s => s.state === "done").length} of ${steps.length} steps done`}>
      <i class="ff-bar-done" style={{ width: share(["done"]) }} />
      <i class="ff-bar-running" style={{ width: share(["running", "waits"]) }} />
      <i class="ff-bar-needs" style={{ width: share(["needs", "problem"]) }} />
    </div>
  );
}

/**
 * A run selected in the tree (design-v2 s4.4; mock-up 05): its header with its review report (in a window of its own) and
 * Run actions, its facts, the steps in it that need
 * you (in tree order; a title selects its step, a button opens its decision), and a Progress board of the runs under it.
 */
export function RunPage({ node, rows, onSelect }: { node: RunNode; rows: readonly Node[]; onSelect: (node: Node) => void }) {
  const { run } = node;
  const detail = useLive([runKey(run.run_id)], () => read(api.GET("/runs/{id}", { params: { path: { id: run.run_id } } })));
  const decisions = useDecision();
  const inside = descendants(node);
  const needs = navigable(inside, "needs");
  const running = navigable(inside, "running");
  const children = inside.filter((n): n is RunNode => n.kind === "run");
  const inputs = Object.entries(detail.data?.inputs ?? {});
  const pullRequests: readonly Schema<"PullRequestLink">[] = run.pull_requests ?? [];

  return (
    <div class="ff-stack" data-run={run.run_id}>
      <div class="ff-runhdr">
        <h1>
          {run.level && <span class={`ff-kind ff-kind-${levels.includes(run.level) ? run.level : "other"}`}>{run.level}</span>}
          <span class="ff-run-title">{node.label}</span>
          <span class={node.state === "needs" ? "ff-pill ff-pill-wait" : node.state === "done" ? "ff-pill ff-pill-ok" : "ff-pill ff-pill-neutral"}>
            {node.state === "needs" ? "needs you" : run.status}
          </span>
          <span class="ff-right-end">
            <button type="button" class="ff-btn ff-btn-ghost" title="Opens in a window of its own"
              onClick={() => links.openWindow(links.review(rows[0].runId, run.run_id), `review:${run.run_id}`)}>
              Review report ⧉
            </button>
            {detail.data && <RunActions run={run} detail={detail.data} />}
          </span>
        </h1>
      </div>
      {detail.error && <ErrorBox error={detail.error} />}
      <div class="ff-box ff-facts" aria-label="Facts">
        <div>
          <div class="ff-k">Tracker</div>
          {run.tracker_issue ? (
            run.tracker_issue.url ? (
              <a href={links.external(run.tracker_issue.url)} target="_blank" rel="noopener noreferrer">
                {run.tracker_issue.key} ↗
              </a>
            ) : (
              run.tracker_issue.key
            )
          ) : (
            <span class="ff-muted">none</span>
          )}
        </div>
        <div>
          <div class="ff-k">Repositories</div>
          {run.repositories.length === 0 ? <span class="ff-muted">none</span> : run.repositories.join(", ")}
        </div>
        <div>
          <div class="ff-k">Template</div>
          <span class="ff-run-template">{run.template}</span>
        </div>
        <div>
          <div class="ff-k">Inputs</div>
          {inputs.length === 0 ? (
            <span class="ff-muted">none</span>
          ) : (
            <span class="ff-text">{inputs.map(([k, v]) => `${k}: ${v}`).join("\n")}</span>
          )}
        </div>
        <div>
          <div class="ff-k">Started</div>
          {run.started_at ? (
            <>
              <Age at={run.started_at} /> by {run.requester} · {took(run.started_at, isActive(run) ? new Date().toISOString() : run.updated_at)}
            </>
          ) : (
            <>by {run.requester}</>
          )}
        </div>
        <div>
          <div class="ff-k">Now</div>
          {running.length} running · {needs.length} {needs.length === 1 ? "needs" : "need"} you
        </div>
        <div>
          <div class="ff-k">Run</div>
          <span class="ff-run-id ff-mono">{run.run_id}</span>
        </div>
        <div>
          <div class="ff-k">Pull requests</div>
          {pullRequests.length === 0 ? (
            <span class="ff-muted">none yet</span>
          ) : (
            pullRequests.map(pr => (
              <div key={`${pr.repository}#${pr.number}`}>
                <a href={links.external(pr.url)} target="_blank" rel="noopener noreferrer">
                  {pr.repository}#{pr.number} ↗
                </a>{" "}
                <span class="ff-muted">{pr.state}</span>
              </div>
            ))
          )}
        </div>
        {run.detail !== null && (
          <div>
            <div class="ff-k">Detail</div>
            <span class="ff-text">{run.detail}</span>
          </div>
        )}
      </div>

      {needs.length > 0 && (
        <section class="ff-box ff-needs" aria-label="Needs you">
          <div class="ff-needs-head">
            <h2>Needs you ({needs.length})</h2>
            <span class="ff-muted ff-right-end">tree order · click a title to go to it</span>
          </div>
          {needs.map(step => (
            <section key={step.key} class="ff-needlist-item" data-key={step.key} data-run={step.runId} data-wait={step.wait?.wait_id}>
              <div>
                <a
                  class="ff-needlist-title"
                  href={links.step(rows[0].runId, step.runId, step.step.id)}
                  onClick={e => {
                    e.preventDefault();
                    onSelect(step);
                  }}
                >
                  {step.wait?.title ?? step.wait?.prompt ?? step.label}
                </a>
                <div class="ff-crumbs">
                  {pathOf(step)} › {step.label}
                </div>
              </div>
              <button type="button" class="ff-btn ff-btn-primary" onClick={() => step.wait && decisions.open(step.runId, step.wait)}>
                {step.wait?.kind === "question" ? "Answer" : "Review and answer"}
              </button>
            </section>
          ))}
        </section>
      )}

      {children.length > 0 && (
        <section class="ff-box" aria-label="Progress">
          <header class="ff-pane-head">
            <h2>Progress</h2>
            <span class="ff-muted">
              {children.length} {children.length === 1 ? "run" : "runs"} planned so far
            </span>
          </header>
          <table class="ff-board">
            <thead>
              <tr>
                <th>Run</th>
                <th>Stage</th>
                <th>Steps</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              {children.map(child => (
                <tr key={child.key} data-run={child.runId}>
                  <td class={runParent(child) !== node ? "ff-board-indent" : undefined}>
                    {child.run.level && (
                      <span class={`ff-kind ff-kind-${levels.includes(child.run.level) ? child.run.level : "other"}`}>{child.run.level}</span>
                    )}
                    <button type="button" class="ff-linkbtn" onClick={() => onSelect(child)}>
                      {child.label}
                    </button>
                  </td>
                  <td>{stageOf(child)}</td>
                  <td>
                    <Bar run={child} />
                  </td>
                  <td class={`ff-state-${child.state}`}>{child.state === "running" || child.state === "done" ? child.run.status : stateWords[child.state]}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
