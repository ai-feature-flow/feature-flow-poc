import type { ComponentChildren } from "preact";
import "./ui.css";

/** A count or short label in a rounded badge: `need` red (waiting on you), `run` accent, `ok` green, `neutral` grey. */
export function Badge({ tone = "neutral", label, children }: { tone?: "need" | "run" | "ok" | "neutral"; label?: string; children: ComponentChildren }) {
  return (
    <span class={`ff-badge ff-badge-${tone}`} aria-label={label}>
      {children}
    </span>
  );
}
