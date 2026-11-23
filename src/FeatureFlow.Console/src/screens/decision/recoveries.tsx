import { useId, useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import type { Answer } from "./AnswerBar";
import { ReasonForm } from "./AnswerBar";
import type { Body } from "./answer";
import { StepPicker } from "./StepPicker";

/**
 * The statuses a person may end a run with: a copy of RunCore.EndableStatuses (Interpreter/Recovery.cs), kept by hand
 * because the API does not send it. The run refuses any other, so a drift shows as a refusal, not a wrong end.
 */
const endable = ["failed", "rejected", "partial", "aborted"];

type Send = (body: Body) => void;

function Grant({ stepId, send, busy }: { stepId: string; send: Send; busy: boolean }) {
  const [attempts, setAttempts] = useState("1");
  const id = useId();
  const n = Number(attempts);
  return (
    <>
      <label for={id}>Grant</label>
      <input id={id} type="number" min={1} step={1} value={attempts} onInput={e => setAttempts(e.currentTarget.value)} />
      <span>more attempts to {stepId}, then retry.</span>
      <button type="button" class="ff-btn ff-btn-primary" disabled={busy || !Number.isInteger(n) || n < 1} onClick={() => send({ action: "grant_attempts", attempts: n })}>
        Grant and retry
      </button>
    </>
  );
}

function ToStep({ recovery, label, does, plan, send, busy }: { recovery: "go_to" | "replan"; label: string; does: string; plan: Schema<"PlanView"> | null; send: Send; busy: boolean }) {
  const [stepId, setStepId] = useState(plan?.steps[0]?.id ?? "");
  const [cleanUp, setCleanUp] = useState(false);
  const id = useId();
  return (
    <>
      <span>{does}</span>
      <label for={id}>Step</label>
      <StepPicker id={id} plan={plan} value={stepId} onChange={setStepId} />
      <label class="ff-dec-inline">
        <input type="checkbox" checked={cleanUp} onChange={e => setCleanUp(e.currentTarget.checked)} /> Clean up: reset the branch first
      </label>
      <button type="button" class="ff-btn ff-btn-primary" disabled={busy || stepId.trim() === ""} onClick={() => send({ action: recovery, step_id: stepId, clean_up: cleanUp })}>
        {label}
      </button>
    </>
  );
}

function End({ send, busy }: { send: Send; busy: boolean }) {
  const [status, setStatus] = useState(endable[0]);
  const id = useId();
  return (
    <ReasonForm does="Stops the run with the status you choose." verb="End the run" busy={busy} onSend={reason => send({ action: "end", status, reason })}>
      <label for={id}>Status</label>
      <select id={id} value={status} onChange={e => setStatus(e.currentTarget.value)}>
        {endable.map(s => (
          <option key={s} value={s}>
            {s}
          </option>
        ))}
      </select>
    </ReasonForm>
  );
}

/** What each recovery does, for the popup's "What each answer does" table. */
export function recoveryEffects(stepId: string): Record<string, [string, string]> {
  return {
    continue: ["Continue", `Starts ${stepId} as planned.`],
    retry: ["Retry", `Runs ${stepId} again with the same prompt and inputs.`],
    grant_attempts: ["Grant attempts", `Adds attempts to ${stepId} and runs it again.`],
    go_to: ["Go to a step", "Continues from the chosen step; with clean up, the branch is reset first."],
    replan: ["Re-plan", "Sends the run back to the chosen planning step with your comment."],
    end: ["End the run", "Stops the run with the status you choose; needs a reason."],
  };
}

/**
 * An intervention's or breakpoint's recoveries as answers (design-v2 s4.5), only those the wait offers (the engine fills
 * its choices, Recoveries.For), in the mock-up's order: Retry · Grant attempts… · Go to a step… · Re-plan… · End the run….
 */
export function recoveryAnswers(wait: Schema<"WaitView">, plan: Schema<"PlanView"> | null, first?: Answer): Answer[] {
  const offered = (choice: string) => wait.choices.includes(choice);
  const answers: Answer[] = first ? [first] : [];
  if (offered("retry")) answers.push({ label: "Retry", tone: first ? undefined : "primary", body: { action: "retry" } });
  if (offered("grant_attempts")) answers.push({ label: "Grant attempts…", form: (send, busy) => <Grant stepId={wait.step_id} send={send} busy={busy} /> });
  if (offered("go_to"))
    answers.push({
      label: "Go to a step…",
      form: (send, busy) => <ToStep recovery="go_to" label="Go to step" does="Continue from" plan={plan} send={send} busy={busy} />,
    });
  if (offered("replan"))
    answers.push({
      label: "Re-plan…",
      form: (send, busy) => <ToStep recovery="replan" label="Re-plan" does="Re-plan from" plan={plan} send={send} busy={busy} />,
    });
  if (offered("end")) answers.push({ label: "End the run…", tone: "danger", form: (send, busy) => <End send={send} busy={busy} /> });
  return answers;
}

/** The "What each answer does" table for the recoveries a wait offers. */
export function Effects({ wait }: { wait: Schema<"WaitView"> }) {
  const effects = recoveryEffects(wait.step_id);
  return (
    <table class="ff-dec-effects">
      <tbody>
        {wait.choices
          .filter(c => c in effects)
          .map(c => (
            <tr key={c}>
              <th scope="row">{effects[c][0]}</th>
              <td>{effects[c][1]}</td>
            </tr>
          ))}
      </tbody>
    </table>
  );
}
