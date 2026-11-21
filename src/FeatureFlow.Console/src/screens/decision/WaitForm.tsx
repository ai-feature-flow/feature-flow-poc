import type { Schema } from "../../api/client";
import * as links from "../../app/links";
import { ErrorBox } from "../../ui/ErrorBox";
import { useAnswer } from "./answer";
import { Choices } from "./Choices";
import { Question } from "./Question";
import { Recovery } from "./Recovery";
import "./WaitForm.css";

export interface WaitFormProps {
  runId: string;
  wait: Schema<"WaitView">;
  /** The run's plan, for route labels and the step picker; null while it loads or when there is none. */
  plan: Schema<"PlanView"> | null;
  /** False: the form is read-only ("you may not answer this"). */
  canAnswer: boolean;
}

/** A min_count decision's answers so far, such as "approved: 1 of 2 people (ann)". */
function progressOf(wait: Schema<"WaitView">): string[] {
  if (wait.min_count <= 1) return [];
  if (wait.progress === null || wait.progress.length === 0) return [`0 of ${wait.min_count} people`];
  return wait.progress.map(p => `${p.choice}: ${p.actors.length} of ${p.needed} people (${p.actors.join(", ")})`);
}

/**
 * An open wait's form (POST /runs/{id}/answers), by kind: a gate's outcomes, an agent's question, a patch approval, or an
 * intervention's or breakpoint's recovery. The run page shows the status waits (stalled, stuck) without a form.
 */
export function WaitForm({ runId, wait, plan, canAnswer }: WaitFormProps) {
  const answer = useAnswer(runId, wait.wait_id);
  const progress = progressOf(wait);

  return (
    <section class="ff-wait" aria-label={`Wait ${wait.wait_id}`} data-wait={wait.wait_id}>
      <h2 class="ff-wait-heading">
        {wait.kind}: {wait.decision_type} at {wait.step_id}
      </h2>
      <p class="ff-wait-prompt">{wait.prompt}</p>
      {wait.consumed.length > 0 && (
        <ul class="ff-wait-consumed" aria-label="Consumed">
          {wait.consumed.map(c => (
            <li key={c.key}>{c.ref === null ? c.name : <a href={links.artifact(runId, c.ref)}>{c.name}</a>}</li>
          ))}
        </ul>
      )}
      {progress.map(line => (
        <p key={line} class="ff-wait-progress">
          {line}
        </p>
      ))}
      {!canAnswer && <p class="ff-wait-note">You may not answer this.</p>}
      <fieldset class="ff-wait-answer" disabled={!canAnswer}>
        {wait.kind === "gate" && <Choices wait={wait} plan={plan} answer={answer} as="outcome" />}
        {wait.kind === "patch_approval" && <Choices wait={wait} plan={plan} answer={answer} as="action" />}
        {wait.kind === "question" && <Question wait={wait} answer={answer} />}
        {(wait.kind === "intervention" || wait.kind === "breakpoint") && <Recovery wait={wait} plan={plan} answer={answer} />}
      </fieldset>
      {answer.pending !== undefined && <p class="ff-wait-pending">Pending: {answer.pending}</p>}
      {answer.error && <ErrorBox error={answer.error} />}
    </section>
  );
}
