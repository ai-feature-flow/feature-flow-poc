import { useId } from "preact/hooks";
import "./Reason.css";

/**
 * A person's comment on an answer or override, sent as its reason (docs/05 section 5; LOG 502). It is optional, and
 * shown to the next agent, except where the choice routes the step as a failure, ends the run or aborts it: there the
 * form says so on the button, keeps it disabled until there is one, and the server refuses one without.
 */
export function Reason({ value, onInput, required = false }: { value: string; onInput: (text: string) => void; required?: boolean }) {
  const id = useId();
  return (
    <div class="ff-reason">
      <label for={id}>Comment</label>
      <textarea id={id} value={value} required={required} rows={2} aria-describedby={id + "-hint"} onInput={e => onInput(e.currentTarget.value)} />
      <small id={id + "-hint"} class="ff-reason-hint">
        {required ? "required" : "optional; shown to the next agent"}
      </small>
    </div>
  );
}

/** Whether `text` is a comment: not empty or only spaces. */
export const hasReason = (text: string): boolean => text.trim().length > 0;

/** The comment to send: the text, or null when there is none, so none is recorded. */
export const reasonOf = (text: string): string | null => (hasReason(text) ? text : null);

/** A choice's label: its name, and that it needs a comment where it does. */
export const labelOf = (choice: string, required: boolean): string => (required ? `${choice} (comment required)` : choice);
