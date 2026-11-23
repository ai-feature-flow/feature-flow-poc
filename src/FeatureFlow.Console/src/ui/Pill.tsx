import type { ComponentChildren } from "preact";
import "./ui.css";

/** A state in a pill, as a run tree's row shows it: `wait` (needs you), `ok` (done) or `neutral`. */
export function Pill({ tone = "neutral", children }: { tone?: "wait" | "ok" | "neutral"; children: ComponentChildren }) {
  return <span class={`ff-pill ff-pill-${tone}`}>{children}</span>;
}
