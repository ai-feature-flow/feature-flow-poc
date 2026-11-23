import type { ComponentChildren } from "preact";
import "./ui.css";

/** A rounded container with a header (title and actions) and a body, as the mock-ups' boxes. */
export function Pane({ title, actions, label, children }: { title?: ComponentChildren; actions?: ComponentChildren; label?: string; children: ComponentChildren }) {
  return (
    <section class="ff-pane" aria-label={label}>
      {(title !== undefined || actions !== undefined) && (
        <header class="ff-pane-head">
          <h2>{title}</h2>
          {actions}
        </header>
      )}
      <div class="ff-pane-body">{children}</div>
    </section>
  );
}
