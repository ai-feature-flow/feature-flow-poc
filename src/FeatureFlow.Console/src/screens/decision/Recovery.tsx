import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { hasReason, Reason } from "../../ui/Reason";
import type { Answering } from "./answer";
import { Field } from "./Field";

/** The statuses a person may end a run with (RunCore.EndableStatuses); the run refuses any other. */
const endable = ["failed", "rejected", "partial", "aborted"];

function Options({ values }: { values: readonly string[] }) {
  return (
    <>
      {values.map(v => (
        <option key={v} value={v}>
          {v}
        </option>
      ))}
    </>
  );
}

/**
 * An intervention or a breakpoint: a recovery from the wait's choices, which the engine fills (Recoveries.For), so
 * `continue` is there at a breakpoint and nowhere else; then the recovery's arguments and the reason.
 */
export function Recovery({ wait, plan, answer }: { wait: Schema<"WaitView">; plan: Schema<"PlanView"> | null; answer: Answering }) {
  const [action, setAction] = useState(wait.choices[0] ?? "");
  const steps = plan?.steps.map(s => s.id) ?? [];
  const [stepId, setStepId] = useState("");
  const [cleanUp, setCleanUp] = useState(false);
  const [attempts, setAttempts] = useState("1");
  const [status, setStatus] = useState(endable[0]);
  const [reason, setReason] = useState("");

  const toStep = action === "go_to" || action === "replan";
  const step = stepId || steps[0] || "";
  const granted = Number(attempts);
  const ready =
    action !== "" && hasReason(reason) && (!toStep || step !== "") && (action !== "grant_attempts" || (Number.isInteger(granted) && granted > 0));

  const send = () =>
    answer.send({
      action,
      reason,
      ...(toStep && { step_id: step, clean_up: cleanUp }),
      ...(action === "grant_attempts" && { attempts: granted }),
      ...(action === "end" && { status }),
    });

  return (
    <>
      <Field label="Recovery">
        {id => (
          <select id={id} value={action} onChange={e => setAction(e.currentTarget.value)}>
            <Options values={wait.choices} />
          </select>
        )}
      </Field>
      {toStep && (
        <>
          <Field label="Step">
            {id =>
              plan === null ? (
                <input id={id} value={stepId} onInput={e => setStepId(e.currentTarget.value)} />
              ) : (
                <select id={id} value={step} onChange={e => setStepId(e.currentTarget.value)}>
                  <Options values={steps} />
                </select>
              )
            }
          </Field>
          <label>
            <input type="checkbox" checked={cleanUp} onChange={e => setCleanUp(e.currentTarget.checked)} /> Clean up: reset the branch first
          </label>
        </>
      )}
      {action === "grant_attempts" && (
        <Field label="Attempts">
          {id => <input id={id} type="number" min={1} step={1} value={attempts} onInput={e => setAttempts(e.currentTarget.value)} />}
        </Field>
      )}
      {action === "end" && (
        <Field label="Status">
          {id => (
            <select id={id} value={status} onChange={e => setStatus(e.currentTarget.value)}>
              <Options values={endable} />
            </select>
          )}
        </Field>
      )}
      <Reason value={reason} onInput={setReason} />
      <button type="button" disabled={!ready || answer.busy} onClick={send}>
        Send
      </button>
    </>
  );
}
