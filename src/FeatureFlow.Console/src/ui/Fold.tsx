import type { ComponentChildren } from "preact";
import { useState } from "preact/hooks";
import "./ui.css";

/**
 * A section that opens and closes (a summary line and its content), such as "Answered by you recently (5)". Content given
 * as a function is built only while the section is open, so a long list of folded sections stays cheap.
 */
export function Fold({ summary, open = false, children }: { summary: ComponentChildren; open?: boolean; children: ComponentChildren | (() => ComponentChildren) }) {
  const [shown, setShown] = useState(open);
  return (
    <details class="ff-fold" open={open} onToggle={e => setShown((e.currentTarget as HTMLDetailsElement).open)}>
      <summary>{summary}</summary>
      {typeof children === "function" ? shown && children() : children}
    </details>
  );
}
