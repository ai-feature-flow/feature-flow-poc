import type { Schema } from "../../api/client";
import { useViewer } from "../../app/viewer";
import { type Answer, AnswerBar, ReasonForm } from "./AnswerBar";
import type { DecisionProps } from "./kinds";

/** Where the plan routes a decision's outcome, when the plan is loaded and says. */
export const routeOf = (plan: Schema<"PlanView"> | null, stepId: string, choice: string): string | null =>
  plan?.steps.find(s => s.id === stepId)?.outcomes.find(o => o.name === choice)?.route ?? null;

/**
 * A gate's outcomes as answers: one that routes the step as a failure (the wait's `reason_required`) opens a form that
 * needs a reason; the rest are sent at once. `labels` renames choices ("approved" → "Approve").
 */
export function gateAnswers(wait: Schema<"WaitView">, does: (choice: string) => string, labels: Record<string, string> = {}): Answer[] {
  let primary = false;
  return wait.choices.map(choice => {
    const label = labels[choice] ?? choice;
    if (wait.reason_required.includes(choice))
      return {
        label: `${label}…`,
        tone: "danger" as const,
        form: (send, busy) => <ReasonForm does={does(choice)} verb={label} busy={busy} onSend={reason => send({ outcome: choice, reason })} />,
      };
    const tone = primary ? undefined : ("primary" as const);
    primary = true;
    return { label, tone, body: { outcome: choice } };
  });
}

/** The artifacts the waiting step consumed, each opened in the viewer. */
export function Consumed({ runId, wait }: { runId: string; wait: Schema<"WaitView"> }) {
  const viewer = useViewer();
  if (wait.consumed.length === 0) return null;
  return (
    <ul class="ff-dec-consumed" aria-label="Consumed">
      {wait.consumed.map(c => (
        <li key={c.key}>
          {c.ref === null ? (
            c.name
          ) : (
            <button type="button" class="ff-linkbtn" onClick={() => viewer.openArtifact({ runId, reference: c.ref!, name: c.name })}>
              {c.name}
            </button>
          )}
        </li>
      ))}
    </ul>
  );
}

/** Any other gate: its outcomes, each beside where the plan routes it; the prompt and what the step consumed. */
export function Gate({ runId, wait, plan, context }: DecisionProps) {
  const does = (choice: string) => {
    const route = routeOf(plan, wait.step_id, choice);
    return route ? `The run goes on to ${route}.` : `${wait.step_id} ends ${choice}.`;
  };
  return (
    <AnswerBar runId={runId} wait={wait} context={context} answers={gateAnswers(wait, does)}>
      <p class="ff-dec-text">{wait.prompt}</p>
      <h3>What each answer does</h3>
      <table class="ff-dec-effects">
        <tbody>
          {wait.choices.map(c => (
            <tr key={c}>
              <th scope="row">{c}</th>
              <td>{does(c)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {wait.consumed.length > 0 && <h3>What it decides on</h3>}
      <Consumed runId={runId} wait={wait} />
    </AnswerBar>
  );
}
