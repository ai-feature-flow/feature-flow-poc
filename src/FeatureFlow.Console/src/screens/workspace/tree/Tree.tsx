import { useEffect, useRef, useState } from "preact/hooks";
import { api, read } from "../../../api/client";
import { invalidate, runKey } from "../../../api/live";
import * as links from "../../../app/links";
import { ErrorBox } from "../../../ui/ErrorBox";
import { Marker } from "../../../ui/Marker";
import { hasReason } from "../../../ui/Reason";
import { ancestors, isActive, lively, type Navigator, navigable, type Node, runOf, type RunNode, spent, type StepNode } from "./model";
import "./Tree.css";

/** How many enclosing rows stay pinned at the top when scrolled (design-v2 Q21). */
const sticky = 4;

/**
 * The rows pinned at the top, as an IDE's sticky scroll pins them: the innermost `sticky` rows enclosing the first row shown
 * whole below them. `shown` is every row on screen in order, each `height` tall from the top of the box scrolled `top`.
 */
function pinnedAt(shown: readonly Node[], top: number, height: number): Node[] {
  const first = shown.find((node, i) => (i - Math.min(sticky, ancestors(node).length)) * height >= top - 1);
  return first ? ancestors(first).slice(-sticky) : [];
}

const symbols = { done: "✓", running: "●", needs: "!", waits: "…", problem: "✗", pending: "○", ghost: "·" } as const;

/** The badge class of a run's level; any other level is plain. */
const levels: readonly string[] = ["project", "epic", "ticket"];

const pills: readonly { kind: Navigator; label: string; icon: string }[] = [
  { kind: "needs", label: "Needs you", icon: "⚠" },
  { kind: "running", label: "Running", icon: "➜" },
  { kind: "problems", label: "Problems", icon: "" },
];

/**
 * Whether the core takes a breakpoint change on the step now (Steering.cs, BreakpointError): cleared at any time, set only
 * on a step neither running nor waiting (a pending patch approval aside), and only while its run runs.
 */
function breakpointable(node: StepNode, run: RunNode): boolean {
  if (!isActive(run.run)) return false;
  const s = node.step.status;
  return node.step.breakpoint || !(s === "running" || s === "retrying" || s === "interrupted" || (s.startsWith("waiting(") && s !== "waiting(patch_approval)"));
}

/**
 * The margin's breakpoint, asked in a small popover beside it: what it does and a reason, which steering needs (Q4), as
 * Steer ▾'s Pause before asks. Escape or Cancel closes it; the engine's refusal shows in it.
 */
function BreakpointAsk({ step, refused, onSend, onCancel }: { step: StepNode; refused?: Error; onSend: (reason: string) => void; onCancel: () => void }) {
  const [reason, setReason] = useState("");
  const input = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    input.current?.focus();
    const escape = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", escape);
    return () => document.removeEventListener("keydown", escape);
  }, []);
  const id = step.step.id;
  return (
    <form
      class="ff-bp-ask"
      aria-label={step.step.breakpoint ? `Clear the breakpoint on ${id}` : `Pause before ${id}`}
      onClick={e => e.stopPropagation()}
      onSubmit={e => {
        e.preventDefault();
        if (hasReason(reason)) onSend(reason);
      }}
    >
      <p>{step.step.breakpoint ? `The run no longer pauses before ${id}.` : `The run pauses before ${id} starts, and waits for you.`}</p>
      <textarea ref={input} rows={2} aria-label="Reason (required)" placeholder="Reason (required)" value={reason} onInput={e => setReason(e.currentTarget.value)} />
      {refused && <ErrorBox error={refused} />}
      <p>
        <button type="submit" class="ff-btn ff-btn-primary" disabled={!hasReason(reason)}>
          {step.step.breakpoint ? "Clear" : "Pause before"}
        </button>{" "}
        <button type="button" class="ff-btn ff-btn-ghost" onClick={onCancel}>
          Cancel
        </button>
      </p>
    </form>
  );
}

export interface TreeProps {
  root: RunNode;
  /** Every row in tree order. */
  rows: readonly Node[];
  selected: string;
  onSelect: (node: Node) => void;
  follow: boolean;
  onFollow: (on: boolean) => void;
}

/**
 * The left pane (design-v2 s4.4, mock-ups 02 and 05): the whole run tree, its navigator pills and Follow current. Rows open
 * by themselves where something runs, waits or is in trouble, and where the selection is; a click on ▾ or ▸ folds a row.
 */
export function Tree({ root, rows, selected, onSelect, follow, onFollow }: TreeProps) {
  const [folds, setFolds] = useState<ReadonlyMap<string, boolean>>(new Map());
  const [nav, setNav] = useState<{ kind: Navigator; index: number; key: string }>();
  const [refused, setRefused] = useState<Error>();
  const [asking, setAsking] = useState<string | null>(null);
  const [scrolled, setScrolled] = useState({ top: 0, height: 34 });
  const box = useRef<HTMLDivElement>(null);

  const selectedNode = rows.find(r => r.key === selected);
  const path = new Set(selectedNode ? ancestors(selectedNode).map(a => a.key) : []);
  const isOpen = (node: Node) => folds.get(node.key) ?? (node.depth === 0 || path.has(node.key) || lively(node));
  const shown: Node[] = [];
  const walk = (node: Node): void => {
    shown.push(node);
    if (node.children.length > 0 && isOpen(node)) node.children.forEach(walk);
  };
  walk(root);
  const pinned = new Map(pinnedAt(shown, scrolled.top, scrolled.height).map((node, i) => [node.key, i]));
  const onScroll = () => {
    const el = box.current;
    if (!el) return;
    const height = el.querySelector<HTMLElement>(".ff-row")?.offsetHeight || 34;
    if (el.scrollTop !== scrolled.top || height !== scrolled.height) setScrolled({ top: el.scrollTop, height });
  };

  // A selection made elsewhere (a pill, a Needs you title, Follow current, a route) opens the rows above it and brings it into view.
  useEffect(() => {
    if (!selectedNode) return;
    const closed = ancestors(selectedNode).filter(a => folds.get(a.key) === false);
    if (closed.length > 0) {
      setFolds(f => new Map([...f, ...closed.map(a => [a.key, true] as const)]));
      return;
    }
    const row = box.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(selected)}"]`);
    const view = box.current?.getBoundingClientRect();
    if (!row || !view) return;
    const at = row.getBoundingClientRect();
    const pinned = Math.min(sticky, ancestors(selectedNode).length) * 34;
    if (at.top < view.top + pinned || at.bottom > view.bottom) row.scrollIntoView({ block: "center" });
  }, [selected, folds]);

  const go = (kind: Navigator) => {
    const list = navigable(rows, kind);
    if (list.length === 0) return;
    const index = nav?.kind === kind && nav.key === selected ? (nav.index + 1) % list.length : 0;
    setNav({ kind, index, key: list[index].key });
    onSelect(list[index]);
  };

  const toggleBreakpoint = async (node: StepNode, reason: string) => {
    setRefused(undefined);
    try {
      await read(
        api.POST("/runs/{id}/overrides", {
          params: { path: { id: node.runId } },
          body: { key: "breakpoint", reason: reason.trim(), step_id: node.step.id, change: { set: String(!node.step.breakpoint) } },
        }),
      );
      setAsking(null);
      await invalidate([runKey(node.runId)]);
    } catch (failure) {
      setRefused(failure instanceof Error ? failure : new Error(String(failure)));
    }
  };

  const row = (node: Node) => {
    const open = node.children.length > 0 && isOpen(node);
    const run = runOf(node);
    const step = node.kind === "step" ? node : null;
    const marker = step?.state === "needs" ? "waiting" : step?.state === "running" ? "running" : undefined;
    const classes = ["ff-row", `ff-row-${node.kind}`];
    const pin = pinned.get(node.key);
    if (pin !== undefined) classes.push("ff-sticky");
    if (step?.state === "needs") classes.push("ff-row-needs");
    if (node.key === selected) classes.push("ff-row-selected");

    return (
      <div
        class={classes.join(" ")}
        style={pin === undefined ? undefined : { top: `${pin * scrolled.height}px`, zIndex: String(9 - pin) }}
        role="treeitem"
        aria-level={node.depth + 1}
        aria-selected={node.key === selected}
        aria-expanded={node.children.length > 0 ? open : undefined}
        data-key={node.key}
        data-run={node.runId}
        data-step={step?.step.id}
        data-state={node.state}
        onClick={() => node.kind !== "ghost" && onSelect(node)}
      >
        <span class="ff-margin">
          {marker ? (
            <Marker kind={marker} />
          ) : step && breakpointable(step, run) ? (
            <button
              type="button"
              class={step.step.breakpoint ? "ff-bp ff-bp-set" : "ff-bp"}
              aria-label={`Breakpoint before ${step.step.id}`}
              aria-pressed={step.step.breakpoint}
              title={step.step.breakpoint ? "Clear the breakpoint" : "Pause before this step"}
              onClick={e => {
                e.stopPropagation();
                setRefused(undefined);
                setAsking(asking === step.key ? null : step.key);
              }}
            />
          ) : (
            step?.step.breakpoint && <Marker kind="breakpoint" />
          )}
          {step && asking === step.key && (
            <BreakpointAsk step={step} refused={refused} onSend={reason => void toggleBreakpoint(step, reason)} onCancel={() => setAsking(null)} />
          )}
        </span>
        <span class={`ff-st ff-st-${node.state}`} aria-hidden="true">
          {symbols[node.state]}
        </span>
        <span class="ff-lbl" style={{ "--depth": String(node.depth) }}>
          {node.children.length > 0 ? (
            <button
              type="button"
              class="ff-tw"
              aria-label={open ? `Fold ${node.label}` : `Open ${node.label}`}
              onClick={e => {
                e.stopPropagation();
                // Folding the branch that holds the selection selects the branch, as a click on it would, and stops Follow
                // current, which would otherwise open it again at the next change inside.
                if (open && path.has(node.key)) {
                  onSelect(node);
                  onFollow(false);
                }
                setFolds(f => new Map(f).set(node.key, !open));
              }}
            >
              {open ? "▾" : "▸"}
            </button>
          ) : (
            <span class="ff-tw" />
          )}
          {node.kind === "run" && node.run.level && (
            <span class={`ff-kind ff-kind-${levels.includes(node.run.level) ? node.run.level : "other"}`}>{node.run.level}</span>
          )}
          <span class="ff-name" title={step?.step.id}>
            {node.label}
          </span>
          {node.note && <span class="ff-sum">{node.note}</span>}
        </span>
        <span class="ff-cnt">
          {step && step.step.attempts_allowed != null && (
            <span class={spent(step.step) ? "ff-used ff-used-spent" : "ff-used"} title="attempts used of allowed">
              {step.step.attempts_used ?? 0}/{step.step.attempts_allowed}
            </span>
          )}
          {node.kind === "run" && node.depth > 0 && (
            <a class="ff-openrun" href={links.own(node.runId)} target="_blank" rel="noopener" title="Open this run on its own" aria-label={`Open ${node.label} on its own`} onClick={e => e.stopPropagation()}>
              ↗
            </a>
          )}
        </span>
      </div>
    );
  };

  const branch = (node: Node) =>
    node.children.length === 0 ? (
      <div key={node.key} role="none">
        {row(node)}
      </div>
    ) : (
      <div key={node.key} class="ff-tg" role="none">
        {row(node)}
        {isOpen(node) && <div role="group">{node.children.map(branch)}</div>}
      </div>
    );

  return (
    <div class="ff-tree">
      <div class="ff-treehead">
        <div class="ff-treehead-title">
          <span>
            Plan · {root.run.level ?? "run"} {root.plan ? `revision ${root.plan.revision}` : ""}
          </span>
          <button type="button" class="ff-follow" aria-pressed={follow} onClick={() => onFollow(!follow)} title="Move the selection to the latest change">
            Follow current{follow ? " ✓" : ""}
          </button>
        </div>
        <div class="ff-pills" role="toolbar" aria-label="Go to">
          {pills.map(p => {
            const count = navigable(rows, p.kind).length;
            const here = nav?.kind === p.kind && nav.key === selected;
            return (
              <button key={p.kind} type="button" class={here ? "ff-chip ff-chip-active" : "ff-chip"} disabled={count === 0} onClick={() => go(p.kind)}>
                {p.icon && <span aria-hidden="true">{p.icon} </span>}
                {p.label} ({here ? `${nav.index + 1}/${count}` : count})
              </button>
            );
          })}
        </div>
        {refused && !asking && <ErrorBox error={refused} />}
      </div>
      <div class="ff-treebox" ref={box} role="tree" aria-label="Run tree" onScroll={onScroll}>
        {branch(root)}
      </div>
    </div>
  );
}
