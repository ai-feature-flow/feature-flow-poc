import type { Schema } from "../../../api/client";

// The run tree as the workspace shows it (plan s5.3): one outline for the whole tree, built from GET /runs/{id}/tree. A run
// holds its plan's elements; a fragment's use is a group; a for_each, parallel or chain step is not a row of its own, its
// branches stand in its place, and a branch of one element is that element; a run.child step is the child run it started,
// or a grey placeholder until it starts. The pills, the root page's lists and a step's path all read this outline in tree
// order, so one order holds everywhere (design-v2 s6, Q24).

type Run = Schema<"RunView">;
type Plan = Schema<"PlanView">;
type Step = Schema<"PlanStepView">;
type Group = Schema<"PlanGroupView">;
type Wait = Schema<"WaitView">;
type TreeNode = Schema<"RunTreeNode">;

/** Where a row stands: done, running, waiting on the caller, waiting on someone else, in trouble, not started, or not planned yet. */
export type State = "done" | "running" | "needs" | "waits" | "problem" | "pending" | "ghost";

interface Base {
  /** Unique in the tree: the row's identity for selection, folding and scrolling. */
  key: string;
  /** The run the row belongs to (its own id for a run's row). */
  runId: string;
  label: string;
  /** A short grey note after the label. */
  note: string;
  state: State;
  /** The rows under it, in plan order. */
  children: Node[];
  parent: Node | null;
  depth: number;
}

export interface RunNode extends Base {
  kind: "run";
  run: Run;
  plan: Plan | null;
  /** Why there is no plan to show, when there is none. */
  planNote: string | null;
  /** Its open waits. */
  waits: Wait[];
}

export interface GroupNode extends Base {
  kind: "group";
  group: Group;
}

export interface StepNode extends Base {
  kind: "step";
  step: Step;
  /** The step's open wait, if it has one. */
  wait: Wait | null;
  /** In trouble: its state, retrying, or its attempts used up before it finished. */
  problem: boolean;
}

export interface GhostNode extends Base {
  kind: "ghost";
}

export type Node = RunNode | GroupNode | StepNode | GhostNode;

export const runRow = (runId: string): string => `r:${runId}`;
export const stepRow = (runId: string, stepId: string): string => `s:${runId}\n${stepId}`;

/** The engine's status waits: shown, never answered (M8 follow-up 11). */
export const statusWaits: readonly string[] = ["stalled", "stuck"];

/** Whether the run still runs, so it takes steering and abort (the API answers 409 to an ended one). */
export const isActive = (run: Run): boolean => run.status === "starting" || run.status === "running";

/** An id as shown: its last part, with spaces for underscores ("verify.fix.verify_fix" is "verify fix"). */
export const shown = (id: string): string => id.slice(id.lastIndexOf(".") + 1).replaceAll("_", " ");

/** A run's name: its tracker issue's title, else the issue's key, else its id. */
export const nameOf = (run: Run): string => run.title ?? run.tracker_issue?.key ?? run.run_id;

/** Whether a step has used all the attempts it is allowed: `used/budget` in red. */
export const spent = (step: Step): boolean =>
  step.attempts_used != null && step.attempts_allowed != null && step.attempts_used >= step.attempts_allowed;

function stepState(step: Step, wait: Wait | null): State {
  if (wait) return statusWaits.includes(wait.kind) ? "problem" : wait.can_answer ? "needs" : "waits";
  const s = step.status;
  if (s.startsWith("waiting(")) return "waits";
  if (s === "running" || s === "retrying") return "running";
  if (s === "exhausted" || s === "escalated" || s === "interrupted" || s === "cancelled") return "problem";
  if (s.startsWith("done(") || step.skipped_with) return "done";
  return "pending";
}

function runState(run: Run): State {
  if (run.status === "completed") return "done";
  return isActive(run) ? "running" : "problem";
}

/** Every row under `node`, not counting it, in tree order. */
export function descendants(node: Node): Node[] {
  const all: Node[] = [];
  const walk = (n: Node) => {
    for (const c of n.children) {
      all.push(c);
      walk(c);
    }
  };
  walk(node);
  return all;
}

/** What rows hold together, the most urgent first; all done is done, nothing started is pending. */
function rolled(rows: readonly Node[]): State {
  const states = new Set(rows.map(r => r.state));
  for (const s of ["needs", "problem", "running", "waits"] as const) if (states.has(s)) return s;
  if (states.size === 0) return "pending";
  if ([...states].every(s => s === "ghost")) return "ghost";
  if ([...states].every(s => s === "done" || s === "ghost")) return "done";
  return states.has("done") ? "running" : "pending";
}

const words = { done: "done", running: "running", needs: "needs you", waits: "waiting", problem: "in trouble", pending: "not started" } as const;

/** "4 tickets: 1 done, 1 running, 1 needs you": how many, then how many are in each state some are in (all alike: just how many). */
function rollUp(noun: string, states: readonly State[]): string {
  const counted = (["done", "running", "needs", "waits", "problem", "pending"] as const)
    .map(s => [s, states.filter(x => x === s).length] as const)
    .filter(([, n]) => n > 0);
  const head = `${states.length} ${noun}`;
  return counted.length > 1 || (counted.length === 1 && counted[0][0] !== "done" && counted[0][0] !== "pending")
    ? `${head}: ${counted.map(([s, n]) => `${n} ${words[s]}`).join(", ")}`
    : head;
}

/**
 * The outline of a plan: each step and group under the group (or container step) it sits in, in the order of its first step
 * in the plan. Elements are step ids and group ids; null is the plan's top level.
 */
function outline(plan: Plan) {
  const steps = new Map(plan.steps.map(s => [s.id, s]));
  const groups = new Map((plan.groups ?? []).map(g => [g.id, g]));
  const children = new Map<string | null, string[]>();
  const placed = new Set<string>();
  const parentOf = (id: string): string | null => {
    const p = steps.get(id)?.parent ?? groups.get(id)?.parent ?? null;
    return p !== null && p !== id && (steps.has(p) || groups.has(p)) ? p : null;
  };
  const place = (id: string, depth: number): void => {
    if (placed.has(id) || depth > 64) return;
    placed.add(id);
    const parent = parentOf(id);
    if (parent !== null) place(parent, depth + 1);
    children.set(parent, [...(children.get(parent) ?? []), id]);
  };
  for (const step of plan.steps) place(step.id, 0);
  return { children, steps, groups };
}

/** The tree's rows from its read: the root run's row, with every other row under it. */
export function build(tree: Schema<"RunTreeView">): RunNode {
  return runNode(tree.root, null, 0);
}

function runNode(source: TreeNode, parent: Node | null, depth: number): RunNode {
  const runId = source.run.run_id;
  const node: RunNode = {
    kind: "run",
    key: runRow(runId),
    runId,
    label: nameOf(source.run),
    note: "",
    state: runState(source.run),
    children: [],
    parent,
    depth,
    run: source.run,
    plan: source.plan,
    planNote: source.plan_note,
    waits: source.waits.filter(w => w.open),
  };
  const childRuns = new Map(source.children.map(c => [c.run.run_id, c]));
  const placed = new Set<string>();

  if (source.plan === null) node.children.push(ghost(runId, "plan", source.plan_note ?? "no plan to show yet", node, depth + 1));
  else {
    const { children, steps, groups } = outline(source.plan);

    /** The rows of one element of the outline under `under`; a container gives its branches' rows; `label` names a branch of one. */
    const rows = (id: string, under: Node, d: number, label?: string): Node[] => {
      const inner = children.get(id) ?? [];
      const step = steps.get(id);
      if (step) {
        if (inner.length > 0 || step.form === "parallel" || step.form === "for_each" || step.form === "chain")
          return inner.length > 0
            ? inner.flatMap(c => rows(c, under, d))
            : [ghost(runId, id, `not planned yet: ${under.kind === "group" ? under.label : shown(id)} appear once ${node.label} plans them`, under, d)];
        if (step.what === "run.child") {
          const child = step.child_run_id ? childRuns.get(step.child_run_id) : undefined;
          if (!child) return [ghost(runId, id, `${label ?? shown(id)}: not started yet`, under, d)];
          placed.add(child.run.run_id);
          return [runNode(child, under, d)];
        }
        const wait = node.waits.find(w => w.step_id === id) ?? null;
        const live = stepState(step, wait);
        // A run that has ended runs nothing and waits on nothing, whatever its steps were last doing.
        const state = !isActive(source.run) && (live === "running" || live === "waits" || live === "needs") ? "pending" : live;
        const row: StepNode = {
          kind: "step",
          key: stepRow(runId, id),
          runId,
          label: shown(id),
          note: label ?? (step.form === "decision" ? "you" : step.form === "agent" ? step.what : ""),
          state,
          children: [],
          parent: under,
          depth: d,
          step,
          wait,
          problem: state === "problem" || step.status === "retrying" || (spent(step) && state !== "done"),
        };
        return [row];
      }
      const group = groups.get(id)!;
      if (group.kind === "branch" && inner.length === 1) return rows(inner[0], under, d, group.label.replaceAll("_", " "));
      const row: GroupNode = {
        kind: "group",
        key: `g:${runId}\n${id}`,
        runId,
        label: label ?? group.label.replaceAll("_", " "),
        note: "",
        state: "pending",
        children: [],
        parent: under,
        depth: d,
        group,
      };
      row.children = inner.flatMap(c => rows(c, row, d + 1));
      return [row];
    };
    node.children = (children.get(null) ?? []).flatMap(id => rows(id, node, depth + 1));
  }
  // Children the plan does not place (no plan, or their step is not in it) follow the plan.
  for (const child of source.children) if (!placed.has(child.run.run_id)) node.children.push(runNode(child, node, depth + 1));

  settle(node);
  return node;
}

function ghost(runId: string, id: string, label: string, parent: Node, depth: number): GhostNode {
  return { kind: "ghost", key: `x:${runId}\n${id}`, runId, label, note: "", state: "ghost", children: [], parent, depth };
}

/** States and notes rolled up from the leaves: a group says what is inside it; a run, whether anything in it needs you. */
function settle(node: Node): void {
  for (const c of node.children) if (c.kind === "group") settle(c);
  if (node.kind === "group") {
    node.state = rolled(node.children);
    const runs = node.children.filter((c): c is RunNode => c.kind === "run");
    const steps = descendants(node).filter(n => n.kind === "step");
    node.note =
      runs.length > 0
        ? rollUp(`${runs[0].run.level ?? "run"}${runs.length === 1 ? "" : "s"}`, runs.map(r => r.state))
        : steps.length > 0
          ? rollUp(steps.length === 1 ? "step" : "steps", steps.map(s => s.state))
          : "";
  } else if (node.kind === "run") {
    // A run that has ended shows how it ended, whatever waits it left open.
    const own = runState(node.run);
    const inside = rolled(node.children);
    node.state = own !== "running" ? own : inside === "needs" || inside === "problem" || inside === "running" || inside === "waits" ? inside : own;
    node.note = node.state === "needs" ? "needs you" : node.run.status;
  }
}

/** Every row in tree order (each before the rows under it). */
export const flatten = (root: Node): Node[] => [root, ...descendants(root)];

/** The rows above `node`, root first. */
export function ancestors(node: Node): Node[] {
  const list: Node[] = [];
  for (let p = node.parent; p !== null; p = p.parent) list.unshift(p);
  return list;
}

/** The run a row belongs to (itself, for a run). */
export function runOf(node: Node): RunNode {
  let n: Node = node;
  while (n.kind !== "run") n = n.parent!;
  return n;
}

/** The path above a row, as a step's detail and the Needs you list show it: "Billing › epics › invoices › tickets". */
export const pathOf = (node: Node): string => ancestors(node).map(a => a.label).join(" › ");

export type Navigator = "needs" | "running" | "problems";

/** The steps a navigator pill goes through, in tree order. */
export function navigable(rows: readonly Node[], kind: Navigator): StepNode[] {
  const steps = rows.filter((r): r is StepNode => r.kind === "step");
  return kind === "needs" ? steps.filter(s => s.state === "needs") : kind === "running" ? steps.filter(s => s.state === "running") : steps.filter(s => s.problem);
}

/** Each step's status by row, to tell what a re-read changed. */
export const statuses = (rows: readonly Node[]): Map<string, string> =>
  new Map(rows.filter((r): r is StepNode => r.kind === "step").map(r => [r.key, r.step.status]));

/**
 * For Follow current: of the steps whose status differs from `before` (a step new since then counts once it has started),
 * the one that changed last; none when nothing changed.
 */
export function changed(before: ReadonlyMap<string, string>, rows: readonly Node[]): StepNode | undefined {
  let best: StepNode | undefined;
  const at = (s: StepNode) => (s.step.changed_at ? Date.parse(s.step.changed_at) : 0);
  for (const r of rows) {
    if (r.kind !== "step" || !isActive(runOf(r).run)) continue;
    const was = before.get(r.key);
    if (was === r.step.status || (was === undefined && r.step.status === "pending")) continue;
    if (!best || at(r) >= at(best)) best = r;
  }
  return best;
}

/** Whether a row is or holds one that needs you, runs or is in trouble: such rows open by themselves. */
export const lively = (node: Node): boolean =>
  [node, ...descendants(node)].some(n => n.state === "needs" || n.state === "running" || n.state === "problem");
