import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { hasReason, labelOf, Reason, reasonOf } from "../../ui/Reason";
import type { Answering } from "./answer";

/** Where the plan routes a decision's outcome, when the plan is loaded and says. */
function routeOf(plan: Schema<"PlanView"> | null, stepId: string, choice: string): string | null {
  return plan?.steps.find(s => s.id === stepId)?.outcomes.find(o => o.name === choice)?.route ?? null;
}

/**
 * A gate's outcomes, or a patch approval's approve and reject: one button per choice, each beside the route the plan
 * gives it, and an optional comment. A choice that routes the step as a failure (the wait's `reason_required`) says it
 * needs a comment and is disabled until there is one. A gate is answered with its outcome, a patch approval with its action.
 */
export function Choices({ wait, plan, answer, as }: { wait: Schema<"WaitView">; plan: Schema<"PlanView"> | null; answer: Answering; as: "outcome" | "action" }) {
  const [reason, setReason] = useState("");
  return (
    <>
      <Reason value={reason} onInput={setReason} />
      <ul class="ff-wait-choices">
        {wait.choices.map(choice => {
          const route = as === "outcome" ? routeOf(plan, wait.step_id, choice) : null;
          const required = wait.reason_required.includes(choice);
          return (
            <li key={choice}>
              <button type="button" disabled={(required && !hasReason(reason)) || answer.busy} onClick={() => answer.send({ [as]: choice, reason: reasonOf(reason) })}>
                {labelOf(choice, required)}
              </button>
              {route !== null && <span class="ff-wait-route">→ {route}</span>}
            </li>
          );
        })}
      </ul>
    </>
  );
}
