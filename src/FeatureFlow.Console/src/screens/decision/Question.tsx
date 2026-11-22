import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { hasReason, Reason, reasonOf } from "../../ui/Reason";
import type { Answering } from "./answer";
import { Field } from "./Field";

/** An agent's question: answered with text (an option the agent offered, or any other), a comment optional (LOG 392). */
export function Question({ wait, answer }: { wait: Schema<"WaitView">; answer: Answering }) {
  const [text, setText] = useState("");
  const [reason, setReason] = useState("");
  return (
    <>
      {wait.choices.length > 0 && (
        <ul class="ff-wait-choices">
          {wait.choices.map(option => (
            <li key={option}>
              <button type="button" onClick={() => setText(option)}>
                {option}
              </button>
            </li>
          ))}
        </ul>
      )}
      <Field label="Answer">{id => <textarea id={id} value={text} rows={3} onInput={e => setText(e.currentTarget.value)} />}</Field>
      <Reason value={reason} onInput={setReason} />
      <button type="button" disabled={!hasReason(text) || answer.busy} onClick={() => answer.send({ outcome: text, reason: reasonOf(reason) })}>
        Send
      </button>
    </>
  );
}
