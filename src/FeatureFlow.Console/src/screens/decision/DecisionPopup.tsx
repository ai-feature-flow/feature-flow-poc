import type { Schema } from "../../api/client";
import { WaitForm } from "./WaitForm";

/**
 * A decision's popup (plan s5.4), opened with usePopup().open(<DecisionPopup …/>). Stub from the console shell (d1):
 * slice 1's form for the wait, until the popups package (e) gives each kind its own.
 */
export function DecisionPopup({ runId, wait, plan = null }: { runId: string; wait: Schema<"WaitView">; plan?: Schema<"PlanView"> | null }) {
  return <WaitForm runId={runId} wait={wait} plan={plan} canAnswer={wait.can_answer} />;
}
