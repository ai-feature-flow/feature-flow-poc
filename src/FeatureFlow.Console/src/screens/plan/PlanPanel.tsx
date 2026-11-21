import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";
import { type Choice, Steer, titleOf } from "./Steer";
import "./PlanPanel.css";

export interface PlanPanelProps {
  runId: string;
  /** The run's plan (GET /runs/{id}/plan); null while it loads or when there is none. */
  plan: Schema<"PlanView"> | null;
  /** Why there is no plan to show, such as a 404 or 409's text. */
  note?: string;
  /** The override keys the caller may use on this run (RunDetail.overrides_allowed). */
  overridesAllowed: readonly string[];
}

/** Statuses of a step that has failed (ApiApp.Console.cs, Statuses); it is retried from its intervention, not from here. */
const failed = (status: string): boolean =>
  status === "waiting(intervention)" || status === "exhausted" || status === "escalated" || status === "interrupted";

/**
 * The run's current plan revision and its steering (POST /runs/{id}/overrides, F18, G11): per step, as the caller may,
 * set or clear a breakpoint, skip with an outcome, or grant attempts; run-wide, amend the plan with a patch. Each opens one
 * form, which needs a reason.
 */
export function PlanPanel({ runId, plan, note, overridesAllowed }: PlanPanelProps) {
  const [choice, setChoice] = useState<Choice>();
  const [reply, setReply] = useState<string>();
  const may = (key: Choice["key"]) => overridesAllowed.includes(key);
  const choose = (next: Choice) => {
    setReply(undefined);
    setChoice(next);
  };

  if (plan === null)
    return (
      <section class="ff-plan" aria-label="Plan">
        <h2>Plan</h2>
        <p>{note ?? "No plan to show yet."}</p>
      </section>
    );

  return (
    <section class="ff-plan" aria-label="Plan">
      <h2>Plan</h2>
      <p>
        Revision {plan.revision}
        {may("amend") && (
          <>
            {" "}
            <button type="button" onClick={() => choose({ key: "amend" })}>
              Amend plan
            </button>
          </>
        )}
      </p>
      <Table head={["Step", "Form", "Status", "Outcomes", ""]}>
        {plan.steps.map(step => (
          <tr key={step.id} data-step={step.id}>
            <td>
              <code>{step.id}</code>
              {step.container && <span class="ff-plan-note">in {step.container}</span>}
            </td>
            <td>
              {step.form}
              <span class="ff-plan-note">{step.what}</span>
            </td>
            <td>
              <Status status={step.status} />
              {step.breakpoint && <span class="ff-plan-note">breakpoint set</span>}
              {step.skipped_with && <span class="ff-plan-note">skipped with {step.skipped_with}</span>}
              {step.protected && <span class="ff-plan-note">protected</span>}
            </td>
            <td>
              <ul class="ff-plan-outcomes">
                {step.outcomes.map(o => (
                  <li key={o.name}>
                    {o.name} ({o.class}){o.route && ` → ${o.route}`}
                  </li>
                ))}
              </ul>
            </td>
            <td class="ff-plan-actions">
              {may("breakpoint") && (
                <button type="button" onClick={() => choose({ key: "breakpoint", step, set: !step.breakpoint })}>
                  {step.breakpoint ? "Clear breakpoint" : "Set breakpoint"}
                </button>
              )}
              {may("skip") && step.outcomes.length > 0 && (
                <button type="button" onClick={() => choose({ key: "skip", step })}>
                  Skip
                </button>
              )}
              {may("grant_attempts") && (
                <button type="button" onClick={() => choose({ key: "grant_attempts", step })}>
                  Grant attempts
                </button>
              )}
            </td>
          </tr>
        ))}
      </Table>
      {plan.steps.some(s => failed(s.status)) && <p class="ff-plan-note">To retry a failed step, choose retry in its intervention under Open waits.</p>}
      {choice && (
        <Steer
          key={titleOf(choice)}
          runId={runId}
          revision={plan.revision}
          choice={choice}
          onDone={result => {
            setChoice(undefined);
            setReply(`${titleOf(choice)}: ${result}`);
          }}
          onCancel={() => setChoice(undefined)}
        />
      )}
      {reply && <p role="status">{reply}</p>}
      <details>
        <summary>Readable form</summary>
        <pre class="ff-plan-readable">{plan.readable}</pre>
      </details>
    </section>
  );
}
