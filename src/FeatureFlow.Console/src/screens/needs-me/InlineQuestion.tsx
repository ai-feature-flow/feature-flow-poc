import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { ErrorBox } from "../../ui/ErrorBox";
import { useAnswer } from "../decision/answer";

/**
 * A question answered in place (design-v2 s4.2, Q26): a text box and the agent's offered options. An option answers with
 * itself at once; Send answers with the text. Once the run takes the answer, the item leaves Needs me.
 */
export function InlineQuestion({ runId, wait, title }: { runId: string; wait: Schema<"WaitView">; title: string }) {
  const answer = useAnswer(runId, wait.wait_id);
  const [text, setText] = useState("");
  const disabled = answer.busy || !wait.can_answer;
  const send = (outcome: string) => answer.send({ outcome, reason: null });

  return (
    <form
      class="ff-quick"
      onSubmit={e => {
        e.preventDefault();
        if (text.trim() !== "") void send(text.trim());
      }}
    >
      <input type="text" aria-label={`Answer: ${title}`} placeholder="Answer here, or pick:" value={text} onInput={e => setText(e.currentTarget.value)} />
      {wait.choices.map(option => (
        <button key={option} type="button" class="ff-btn ff-btn-ghost" disabled={disabled} onClick={() => void send(option)}>
          {option}
        </button>
      ))}
      <button type="submit" class="ff-btn ff-btn-primary" disabled={disabled || text.trim() === ""}>
        Send
      </button>
      {answer.pending && <p class="ff-quick-note">Recorded: {answer.pending}</p>}
      {answer.error && <ErrorBox error={answer.error} />}
    </form>
  );
}
