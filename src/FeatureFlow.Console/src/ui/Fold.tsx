import type { ComponentChildren } from "preact";
import "./ui.css";

/** A section that opens and closes (a summary line and its content), such as "Answered by you recently (5)". */
export function Fold({ summary, open = false, children }: { summary: ComponentChildren; open?: boolean; children: ComponentChildren }) {
  return (
    <details class="ff-fold" open={open}>
      <summary>{summary}</summary>
      {children}
    </details>
  );
}
