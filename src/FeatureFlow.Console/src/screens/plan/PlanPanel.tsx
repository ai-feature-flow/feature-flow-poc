import type { Schema } from "../../api/client";

export interface PlanPanelProps {
  runId: string;
  /** The run's plan (GET /runs/{id}/plan); null while it loads or when there is none. */
  plan: Schema<"PlanView"> | null;
  /** Why there is no plan to show, such as a 404 or 409's text. */
  note?: string;
  /** The override keys the caller may use on this run (RunDetail.overrides_allowed). */
  overridesAllowed: readonly string[];
}

/** The run's plan and steering (POST /runs/{id}/overrides). A placeholder until task T6. */
export function PlanPanel(_: PlanPanelProps) {
  return (
    <section class="ff-plan">
      <h2>Plan</h2>
      <p>The plan panel is not built yet.</p>
    </section>
  );
}
