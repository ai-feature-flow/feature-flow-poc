import { useMemo } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { usePopup } from "../../app/popup";
import { ErrorBox } from "../../ui/ErrorBox";
import { AgentQuestion } from "./AgentQuestion";
import { clock, stepPath } from "./AnswerBar";
import { Breakpoint } from "./Breakpoint";
import { Gate } from "./Gate";
import { Intervention } from "./Intervention";
import { type DecisionProps, kindOf, titleOf, wideOf } from "./kinds";
import { Merge } from "./Merge";
import { PatchApproval } from "./PatchApproval";
import { Session } from "./Session";
import { SpecApproval } from "./SpecApproval";
import { Triage } from "./Triage";
import "./decision.css";

const bodies = {
  intervention: Intervention,
  triage: Triage,
  spec: SpecApproval,
  merge: Merge,
  patch: PatchApproval,
  breakpoint: Breakpoint,
  question: AgentQuestion,
  session: Session,
  gate: Gate,
} satisfies Record<ReturnType<typeof kindOf>, (props: DecisionProps) => unknown>;

/** The run as a person names it: its tracker issue and title, else its id. */
function runLabel(run: Schema<"RunView">): string {
  const issue = run.tracker_issue;
  if (issue) return issue.title ? `${issue.key} ${issue.title}` : issue.key;
  return run.title ?? run.run_id;
}

/**
 * A decision's popup body (design-v2 s4.5; plan s5.4), by the wait's kind and decision type. It reads the wait's run (and
 * reads it again on every change of it), so the wait shown is the latest: once answered elsewhere it says it is closed.
 * Open it with useDecision().open(runId, wait, tree).
 */
export function DecisionPopup({ runId, wait, tree }: { runId: string; wait: Schema<"WaitView">; tree?: string }) {
  const live = useLive([runKey(runId)], async () => {
    const [detail, plan] = await Promise.all([
      read(api.GET("/runs/{id}", { params: { path: { id: runId } } })),
      read(api.GET("/runs/{id}/plan", { params: { path: { id: runId } } })).catch(() => undefined),
    ]);
    return { detail, plan: plan ?? null };
  });
  if (live.data === undefined) return live.error ? <ErrorBox error={live.error} /> : <p class="ff-dec-muted">Reading the decision…</p>;
  const { detail, plan } = live.data;
  const current = detail.waits.find(w => w.wait_id === wait.wait_id) ?? wait;
  const context = [runLabel(detail.run), stepPath(current.step_id), current.decision_type || current.kind, `opened ${clock(current.updated_at)}`].join(" · ");
  const Body = bodies[kindOf(current)];
  return <Body runId={runId} tree={tree} wait={current} detail={detail} plan={plan} context={context} />;
}

export interface Decisions {
  /** Opens the wait's decision popup over the page; `tree` is the root run of the tree it is opened in, if any. */
  open(runId: string, wait: Schema<"WaitView">, tree?: string): void;
}

/** Opens decision popups, titled and sized by the wait's kind; every screen opens a decision through this. */
export function useDecision(): Decisions {
  const popups = usePopup();
  return useMemo(
    () => ({
      open: (runId, wait, tree) => popups.open(<DecisionPopup runId={runId} wait={wait} tree={tree} />, { label: titleOf(wait), wide: wideOf(wait) }),
    }),
    [popups],
  );
}
