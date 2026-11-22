import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";
import { type Choice, outcomesOf, Steer, titleOf } from "./Steer";
import "./PlanPanel.css";

type Step = Schema<"PlanStepView">;

interface PlanPanelProps {
  runId: string;
  /** The run's plan (GET /runs/{id}/plan); null while it loads or when there is none. */
  plan: Schema<"PlanView"> | null;
  /** Why there is no plan to show, such as a 404 or 409's text. */
  note?: string;
  /** The override keys the caller may use on this run (RunDetail.overrides_allowed); none once the run has ended. */
  overridesAllowed: readonly string[];
}

/** Statuses of a step that has failed (ApiApp.Console.cs, Statuses); it is retried from its intervention, not from here. */
const failed = (status: string): boolean =>
  status === "waiting(intervention)" || status === "exhausted" || status === "escalated" || status === "interrupted";

/** A step the core counts as running: in flight, or to be resumed or retried. */
const running = (status: string): boolean => status === "running" || status === "retrying" || status === "interrupted";

/**
 * The steering the core accepts on a step now (Steering.cs: BreakpointError, SkipError, GrantError), so the panel offers
 * nothing it would refuse: a breakpoint is set only on a step that is neither running nor waiting, and cleared at any time;
 * a step is skipped unless it is running or has a patch awaiting approval, and a protected one only with skip_protected;
 * attempts are granted only to a step that uses them (an agent or an action).
 */
function offers(step: Step, may: (key: string) => boolean): Choice[] {
  const waiting = step.status.startsWith("waiting(");
  const choices: Choice[] = [];
  if (may("breakpoint") && (step.breakpoint || !(running(step.status) || (waiting && step.status !== "waiting(patch_approval)"))))
    choices.push({ key: "breakpoint", step, set: !step.breakpoint });
  if (
    may("skip") &&
    outcomesOf(step).length > 0 &&
    !running(step.status) &&
    step.status !== "waiting(patch_approval)" &&
    (!step.protected || may("skip_protected"))
  )
    choices.push({ key: "skip", step });
  if (may("grant_attempts") && (step.form === "agent" || step.form === "action")) choices.push({ key: "grant_attempts", step });
  return choices;
}

/** A choice's button label. */
const labelOf = ({ key, set }: Choice): string =>
  key === "breakpoint" ? (set ? "Set breakpoint" : "Clear breakpoint") : key === "skip" ? "Skip" : "Grant attempts";

/**
 * The run's current plan revision and its steering (POST /runs/{id}/overrides, F18, G11): per step, as the caller may and
 * the core accepts, set or clear a breakpoint, skip with an outcome, or grant attempts; run-wide, amend the plan with a
 * patch. Each opens one form where it was chosen, with an optional comment.
 */
export function PlanPanel({ runId, plan, note, overridesAllowed }: PlanPanelProps) {
  const [choice, setChoice] = useState<Choice>();
  const [reply, setReply] = useState<{ stepId?: string; text: string }>();
  const may = (key: string) => overridesAllowed.includes(key);
  const choose = (next: Choice) => {
    setReply(undefined);
    setChoice(next);
  };

  if (plan === null)
    return (
      <section class="ff-plan ff-run-section" aria-label="Plan">
        <h2>Plan</h2>
        <p>{note ?? "No plan to show yet."}</p>
      </section>
    );

  const steer = (chosen: Choice) => (
    <Steer
      key={titleOf(chosen)}
      runId={runId}
      revision={plan.revision}
      choice={chosen}
      onDone={result => {
        setChoice(undefined);
        setReply({ stepId: chosen.step?.id, text: `${titleOf(chosen)}: ${result}` });
      }}
      onCancel={() => setChoice(undefined)}
    />
  );

  /** The open form, or the last reply, where its choice was made: under its step's row, or under Amend plan. */
  const here = (stepId?: string) => {
    const form = choice !== undefined && choice.step?.id === stepId;
    const said = reply !== undefined && reply.stepId === stepId;
    return (
      (form || said) && (
        <>
          {form && steer(choice)}
          {said && <p role="status">{reply.text}</p>}
        </>
      )
    );
  };

  return (
    <section class="ff-plan ff-run-section" aria-label="Plan">
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
      {here(undefined)}
      <Table head={["Step", "Form", "Status", "Outcomes", ""]}>
        {plan.steps.map(step => [
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
              {offers(step, may).map(offer => (
                <button key={offer.key} type="button" onClick={() => choose(offer)}>
                  {labelOf(offer)}
                </button>
              ))}
            </td>
          </tr>,
          here(step.id) && (
            <tr key={`${step.id}\nsteer`} class="ff-plan-steer-row">
              <td colSpan={5}>{here(step.id)}</td>
            </tr>
          ),
        ])}
      </Table>
      {plan.steps.some(s => failed(s.status)) && <p class="ff-plan-note">To retry a failed step, choose retry in its intervention under Open waits.</p>}
      <details>
        <summary>Readable form</summary>
        <pre class="ff-plan-readable">{plan.readable}</pre>
      </details>
    </section>
  );
}
