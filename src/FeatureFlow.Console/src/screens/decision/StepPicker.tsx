import type { Schema } from "../../api/client";

/** The label of the group a step sits in: its innermost group (a fragment use or a branch), its container step, or none. */
function groupOf(plan: Schema<"PlanView">, step: Schema<"PlanStepView">): string {
  const parent = step.parent ?? step.container;
  if (!parent) return "Run";
  return plan.groups?.find(g => g.id === parent)?.label ?? parent;
}

/**
 * A step chosen from the plan, grouped as the tree groups it (design-v2 s4.5: "Go to a step… (grouped picker)"): one
 * option group per fragment use, branch or container, in plan order. Without a plan, the step id is typed.
 */
export function StepPicker({ id, plan, value, onChange }: { id: string; plan: Schema<"PlanView"> | null; value: string; onChange: (stepId: string) => void }) {
  if (plan === null) return <input id={id} value={value} placeholder="step id" onInput={e => onChange(e.currentTarget.value)} />;
  const groups: { label: string; steps: string[] }[] = [];
  for (const step of plan.steps) {
    const label = groupOf(plan, step);
    const last = groups[groups.length - 1];
    if (last?.label === label) last.steps.push(step.id);
    else groups.push({ label, steps: [step.id] });
  }
  return (
    <select id={id} value={value} onChange={e => onChange(e.currentTarget.value)}>
      {groups.map((g, i) => (
        <optgroup key={`${g.label}-${i}`} label={g.label}>
          {g.steps.map(s => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </optgroup>
      ))}
    </select>
  );
}
