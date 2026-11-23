import { useMemo } from "preact/hooks";
import type { Schema } from "../../api/client";
import { usePopup } from "../../app/popup";
import { titleOf, wideOf } from "./kinds";
import { WaitForm } from "./WaitForm";

/**
 * A decision's popup (plan s5.4). Open it with useDecision().open(runId, wait), which gives the popup its title and width
 * (design-v2 s4.5). Stub body: slice 1's form for the wait, until each kind has its own.
 */
export function DecisionPopup({ runId, wait, plan = null }: { runId: string; wait: Schema<"WaitView">; plan?: Schema<"PlanView"> | null }) {
  return <WaitForm runId={runId} wait={wait} plan={plan} canAnswer={wait.can_answer} />;
}

export interface Decisions {
  /** Opens the wait's decision popup over the page. */
  open(runId: string, wait: Schema<"WaitView">): void;
}

/** Opens decision popups, titled and sized by the wait's kind; every screen opens a decision through this. */
export function useDecision(): Decisions {
  const popups = usePopup();
  return useMemo(
    () => ({
      open: (runId, wait) => popups.open(<DecisionPopup runId={runId} wait={wait} />, { label: titleOf(wait), wide: wideOf(wait) }),
    }),
    [popups],
  );
}
