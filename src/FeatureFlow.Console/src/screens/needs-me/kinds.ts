import type { Schema } from "../../api/client";
import { titleOf } from "../decision/kinds";

type Wait = Schema<"WaitView">;

/** The kinds Needs me filters by (design-v2 s4.2), each as one item's tag names it. */
export type Kind = "approval" | "intervention" | "question" | "triage";

export const kinds: readonly (readonly [Kind, string])[] = [
  ["approval", "Approvals"],
  ["intervention", "Interventions"],
  ["question", "Questions"],
  ["triage", "Triage"],
];

/**
 * A wait's kind for a person: a findings triage gate is triage; any other gate and a patch approval are approvals; a step
 * stopped for a person (an intervention, a breakpoint, a status wait) is an intervention; a question is a question.
 */
export function kindOf(wait: Wait): Kind {
  switch (wait.kind) {
    case "question":
      return "question";
    case "intervention":
    case "breakpoint":
    case "stalled":
    case "stuck":
      return "intervention";
    default:
      return wait.decision_type === "findings.triage" ? "triage" : "approval";
  }
}

/** The text's first line. */
const firstLine = (text: string): string => text.split("\n", 1)[0].trim();

/** The one-line summary under it: the wait's summary, else its prompt's first line (unless that is the title already). */
export function summaryOf(wait: Wait): string | null {
  if (wait.summary) return wait.summary;
  const line = firstLine(wait.prompt);
  return line === titleOf(wait) ? null : line;
}
