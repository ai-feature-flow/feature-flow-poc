import type { Schema } from "../api/client";

/** A min_count decision's answers so far, a line per choice, such as "approved: 1 of 2 people (ann)"; none when one answer decides. */
export function progress(wait: Schema<"WaitView">): string[] {
  if (wait.min_count <= 1) return [];
  if (wait.progress === null || wait.progress.length === 0) return [`0 of ${wait.min_count} people`];
  return wait.progress.map(p => `${p.choice}: ${p.actors.length} of ${p.needed} people (${p.actors.join(", ")})`);
}
