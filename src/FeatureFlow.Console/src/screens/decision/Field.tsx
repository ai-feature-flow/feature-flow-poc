import type { ComponentChildren } from "preact";
import { useId } from "preact/hooks";

/**
 * A labelled control. The label names the control by `for`, not by wrapping it, so a select's name is its label alone,
 * not its label and every option's text.
 */
export function Field({ label, children }: { label: string; children: (id: string) => ComponentChildren }) {
  const id = useId();
  return (
    <div class="ff-wait-field">
      <label for={id}>{label}</label>
      {children(id)}
    </div>
  );
}
