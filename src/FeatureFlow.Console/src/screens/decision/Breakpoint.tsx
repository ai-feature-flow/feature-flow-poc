import type { Schema } from "../../api/client";
import { override } from "../steer/override";
import { AnswerBar } from "./AnswerBar";
import type { DecisionProps } from "./kinds";
import { Effects, recoveryAnswers } from "./recoveries";

/** The step a wait is at, as the plan has it: what it is and where each outcome goes. */
export function StepFacts({ step }: { step: Schema<"PlanStepView"> | undefined }) {
  if (!step) return null;
  return (
    <dl class="ff-dec-facts">
      <dt>Step</dt>
      <dd>
        {step.id} ({step.form})
      </dd>
      <dt>What it does</dt>
      <dd class="ff-dec-text">{step.what}</dd>
      <dt>Outcomes</dt>
      <dd>
        {step.outcomes.map(o => (
          <div key={o.name}>
            {o.name}
            {o.route ? ` → ${o.route}` : ""}
          </div>
        ))}
      </dd>
    </dl>
  );
}

/** A breakpoint (design-v2 s4.5): Continue · Clear and continue · the recoveries; the step and what it would do. */
export function Breakpoint({ runId, wait, plan, context }: DecisionProps) {
  const step = plan?.steps.find(s => s.id === wait.step_id);
  const answers = recoveryAnswers(wait, plan, { label: "Continue", tone: "primary", body: { action: "continue" } });
  answers.splice(1, 0, {
    label: "Clear and continue",
    prepare: reason => override(runId, { key: "breakpoint", step_id: wait.step_id, reason, change: { set: "false" } }),
    body: { action: "continue" },
  });
  return (
    <AnswerBar runId={runId} wait={wait} context={context} answers={answers}>
      <h3>Paused before</h3>
      <p class="ff-dec-text">{wait.prompt}</p>
      <StepFacts step={step} />
      <h3>What each answer does</h3>
      <Effects wait={wait} />
    </AnswerBar>
  );
}
