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

/** The popup's title: the API's (a wait's title, design-v2 s4.5's table; `WaitTitleOf` always sets it), else its prompt. */
export const titleOf = (wait: Wait): string => wait.title ?? wait.prompt;

/** Whether the popup is the wide one: those with tables of evidence. */
export const wideOf = (wait: Wait): boolean => ["triage", "spec", "merge", "patch", "intervention"].includes(kindOf(wait));
