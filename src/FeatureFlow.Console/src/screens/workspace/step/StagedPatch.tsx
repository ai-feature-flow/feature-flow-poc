import type { Schema } from "../../../api/client";

/**
 * The plan patch a running step's agent has staged (A8), from the step detail's `staged_patch`: its target, revision and
 * operations, and the plan as it would become (`GET /runs/{id}/plan?staged={step}`). A stub until build-console-b2 package D
 * builds it: it renders nothing.
 */
export function StagedPatch({ runId, stepId, staged }: { runId: string; stepId: string; staged: Schema<"StagedPatchView"> }) {
  void runId;
  void stepId;
  void staged;
  return null;
}
