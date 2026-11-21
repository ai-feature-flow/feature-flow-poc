import type { Schema } from "../../api/client";

export interface WaitFormProps {
  runId: string;
  wait: Schema<"WaitView">;
  /** The run's plan, for route labels and the step picker; null while it loads or when there is none. */
  plan: Schema<"PlanView"> | null;
  /** False: the form is read-only ("you may not answer this"). */
  canAnswer: boolean;
}

/** An open wait's form (POST /runs/{id}/answers). A placeholder until task T5. */
export function WaitForm({ wait }: WaitFormProps) {
  return (
    <section class="ff-wait" data-wait={wait.wait_id}>
      <p>Answering this wait is not built yet.</p>
    </section>
  );
}
