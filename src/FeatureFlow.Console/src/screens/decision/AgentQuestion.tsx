import { useId, useState } from "preact/hooks";
import { hasReason } from "../../ui/Reason";
import { type Answer, AnswerBar } from "./AnswerBar";
import type { Body } from "./answer";
import type { DecisionProps } from "./kinds";

function Write({ send, busy }: { send: (body: Body) => void; busy: boolean }) {
  const [text, setText] = useState("");
  const id = useId();
  return (
    <>
      <label for={id}>Answer</label>
      <textarea id={id} rows={3} value={text} onInput={e => setText(e.currentTarget.value)} />
      <button type="button" class="ff-btn ff-btn-primary" disabled={busy || !hasReason(text)} onClick={() => send({ outcome: text })}>
        Send
      </button>
    </>
  );
}

/**
 * An agent's question (design-v2 s4.5): the answer written, or one of the options the agent offered, each sent at once; a
 * comment optional (LOG 392). The question in full, and which of the step's questions it is.
 */
export function AgentQuestion({ runId, wait, detail, context }: DecisionProps) {
  const asked = detail.waits.filter(w => w.kind === "question" && w.step_id === wait.step_id);
  const n = asked.findIndex(w => w.wait_id === wait.wait_id) + 1;
  const answers: Answer[] = [
    { label: "Write an answer", tone: "primary", form: (send, busy) => <Write send={send} busy={busy} /> },
    ...wait.choices.map(option => ({ label: option, body: { outcome: option } })),
  ];
  return (
    <AnswerBar runId={runId} wait={wait} context={context} answers={answers} initiallyOpen={0}>
      <h3>
        The question{n > 0 && asked.length > 1 ? ` (question ${n} of ${asked.length} from ${wait.step_id} so far)` : ""}
      </h3>
      <p class="ff-dec-text">{wait.prompt}</p>
      {wait.choices.length > 0 && <p class="ff-dec-muted">The agent offered: {wait.choices.join(" · ")}. Any other answer may be written.</p>}
    </AnswerBar>
  );
}
