import "./Reason.css";

/**
 * A person's reason for an answer or override (docs/05 section 5). The server refuses a missing reason where one is
 * required; a form also keeps its button disabled until there is one.
 */
export function Reason({ value, onInput, required = true }: { value: string; onInput: (text: string) => void; required?: boolean }) {
  return (
    <label class="ff-reason">
      {required ? "Reason" : "Reason (optional)"}
      <textarea value={value} required={required} rows={2} onInput={e => onInput(e.currentTarget.value)} />
    </label>
  );
}

/** Whether `text` is a reason: not empty or only spaces. */
export const hasReason = (text: string): boolean => text.trim().length > 0;
