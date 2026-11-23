import type { Schema } from "../../api/client";

type Wait = Schema<"WaitView">;

/** What every decision popup's body is given: the wait (as last read), its run's detail and plan (null when none), and the line under its title. */
export interface DecisionProps {
  runId: string;
  wait: Wait;
  detail: Schema<"RunDetail">;
  plan: Schema<"PlanView"> | null;
  context: string;
}

/** Which popup a wait opens (design-v2 s4.5; plan s5.4), by its kind and decision type. */
export type DecisionKind = "intervention" | "triage" | "spec" | "merge" | "patch" | "breakpoint" | "question" | "session" | "gate";

export function kindOf(wait: Wait): DecisionKind {
  switch (wait.kind) {
    case "intervention":
      return "intervention";
    case "patch_approval":
      return "patch";
    case "breakpoint":
      return "breakpoint";
    case "question":
      return "question";
  }
  if (wait.session_id) return "session";
  if (wait.decision_type === "findings.triage") return "triage";
  if (wait.decision_type.endsWith(".spec_approval")) return "spec";
  if (wait.decision_type.endsWith(".merge_action")) return "merge";
  return "gate";
}

/** The popup's title: the API's (a wait's title, design-v2 s4.5's table) when it gives one, else the table's rule from what the wait holds. */
export function titleOf(wait: Wait): string {
  if (wait.title) return wait.title;
  const step = wait.step_id;
  switch (kindOf(wait)) {
    case "intervention": {
      const cause = wait.cause;
      if (cause && cause.attempts_used !== null && cause.attempts_allowed !== null && cause.attempts_used >= cause.attempts_allowed)
        return `${step} has used all its attempts`;
      return `${step} needs you: ${cause?.reason ?? wait.prompt}`;
    }
    case "triage":
      return "Triage the findings the triager could not clear";
    case "spec":
      return `Approve the plan at ${step}`;
    case "merge": {
      const pr = wait.pull_request;
      return pr ? `Merge ${pr.repository}#${pr.number} into ${pr.base}?` : `Merge at ${step}?`;
    }
    case "patch":
      return `${wait.patch?.proposer_step_id ?? step} proposes a plan change`;
    case "breakpoint":
      return `Paused before ${step}`;
    case "question":
      return `${step} asks`;
    case "session":
      return `Your planning session for ${step}`;
    case "gate":
      return `${wait.decision_type || wait.kind} at ${step}`;
  }
}

/** Whether the popup is the wide one: those with tables of evidence. */
export const wideOf = (wait: Wait): boolean => ["triage", "spec", "merge", "patch", "intervention"].includes(kindOf(wait));
