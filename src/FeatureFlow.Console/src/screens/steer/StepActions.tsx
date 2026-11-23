import type { Schema } from "../../api/client";
import { usePopup } from "../../app/popup";
import { Menu } from "../../ui/Menu";
import { type Choice, Steer, titleOf } from "../plan/Steer";

/**
 * A step's Steer ▾ and Edit step (design-v2 s4.4), shown only in the step's detail. Stub from the console shell (d1):
 * slice 1's override forms in a popup, until the popups and steering package (e) previews each consequence and asks a
 * reason. Go to a step and re-plan are disabled in B1 (plan s2.3).
 */
export function StepActions({ runId, plan, step, overridesAllowed }: {
  runId: string;
  plan: Schema<"PlanView">;
  step: Schema<"PlanStepView">;
  overridesAllowed: readonly string[];
}) {
  const popups = usePopup();
  const may = (key: string) => overridesAllowed.includes(key);
  const steer = (choice: Choice) =>
    popups.open(<Steer runId={runId} revision={plan.revision} choice={choice} onDone={() => popups.close()} onCancel={() => popups.close()} />, {
      label: titleOf(choice),
    });
  const later = "only at an intervention (B2)";

  return (
    <span class="ff-step-actions">
      <Menu
        label="Steer"
        items={[
          { label: "Pause before", disabled: !may("breakpoint"), onSelect: () => steer({ key: "breakpoint", step, set: true }) },
          { label: "Skip", disabled: !may("skip"), onSelect: () => steer({ key: "skip", step }) },
          { label: "Grant attempts", disabled: !may("grant_attempts"), onSelect: () => steer({ key: "grant_attempts", step }) },
          { label: "Go to a step", disabled: true, note: later, onSelect: () => undefined },
          { label: "Re-plan", disabled: true, note: later, onSelect: () => undefined },
        ]}
      />{" "}
      <button type="button" class="ff-btn ff-btn-ghost" disabled={!may("amend")} onClick={() => steer({ key: "amend" })}>
        Edit step
      </button>
    </span>
  );
}
