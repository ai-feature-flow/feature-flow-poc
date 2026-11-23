import type { Schema } from "../../api/client";
import { usePopup } from "../../app/popup";
import { Menu, type MenuItem } from "../../ui/Menu";
import { outcomesOf, type SteerKind, SteerForm, titleOf } from "./SteerForm";
import "./steer.css";

type Step = Schema<"PlanStepView">;

/** A step the core counts as running: in flight, or to be resumed or retried. */
const running = (status: string): boolean => status === "running" || status === "retrying" || status === "interrupted";

/**
 * Why the core would refuse a steer on the step now (Steering.cs: BreakpointError, SkipError, GrantError), or null: a
 * breakpoint is set only on a step that is neither running nor waiting, and cleared at any time; a step is skipped unless
 * it is running or has a patch awaiting approval, and a protected one only with skip_protected; attempts go only to a
 * step that uses them (an agent or an action).
 */
function refusal(kind: SteerKind, step: Step, may: (key: string) => boolean): string | null {
  const waiting = step.status.startsWith("waiting(");
  switch (kind) {
    case "breakpoint":
      if (!may("breakpoint")) return "you may not set breakpoints here";
      return step.breakpoint || !(running(step.status) || (waiting && step.status !== "waiting(patch_approval)")) ? null : "only before the step starts";
    case "skip":
      if (!may("skip")) return "you may not skip steps here";
      if (step.protected && !may("skip_protected")) return "a protected step";
      if (outcomesOf(step).length === 0) return "it has no outcome to skip with";
      return running(step.status) || step.status === "waiting(patch_approval)" ? "not while it runs" : null;
    case "grant_attempts":
      if (!may("grant_attempts")) return "you may not grant attempts here";
      return step.form === "agent" || step.form === "action" ? null : "a decision uses no attempts";
    case "edit":
      return may("amend") || may("change_role") ? null : "you may not amend the plan here";
  }
}

/**
 * A step's Steer ▾ and Edit step (design-v2 s4.4), shown only in the step's detail, never on tree rows. Steer ▾: pause
 * before (or clear the breakpoint), skip, grant attempts; go to a step and re-plan are disabled in B1, being recoveries at an
 * intervention only (plan s2.3). Edit step: amend the step's parameters or limits for its next attempt, or change its role.
 * Each opens a form that previews its consequence and needs a reason; one the core would refuse now is disabled with why.
 */
export function StepActions({ runId, plan, step, overridesAllowed }: {
  runId: string;
  plan: Schema<"PlanView">;
  step: Step;
  overridesAllowed: readonly string[];
}) {
  const popups = usePopup();
  const may = (key: string) => overridesAllowed.includes(key);
  const steer = (kind: SteerKind) =>
    popups.open(<SteerForm runId={runId} plan={plan} step={step} kind={kind} roles={may("change_role")} />, { label: titleOf(kind, step) });
  const item = (kind: SteerKind, label: string): MenuItem => {
    const why = refusal(kind, step, may);
    return { label, disabled: why !== null, note: why ?? undefined, onSelect: () => steer(kind) };
  };
  const later = "only at an intervention (B2)";
  const edit = refusal("edit", step, may);

  return (
    <span class="ff-step-actions">
      <Menu
        label="Steer"
        items={[
          item("breakpoint", step.breakpoint ? "Clear the breakpoint" : "Pause before"),
          item("skip", "Skip"),
          item("grant_attempts", "Grant attempts"),
          { label: "Go to a step", disabled: true, note: later, onSelect: () => undefined },
          { label: "Re-plan", disabled: true, note: later, onSelect: () => undefined },
        ]}
      />{" "}
      <button type="button" class="ff-btn ff-btn-ghost" disabled={edit !== null} title={edit ?? undefined} onClick={() => steer("edit")}>
        Edit step
      </button>
    </span>
  );
}
