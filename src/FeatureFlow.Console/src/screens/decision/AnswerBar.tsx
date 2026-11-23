import type { ComponentChildren } from "preact";
import { useState } from "preact/hooks";
import { ApiError, type Schema } from "../../api/client";
import { invalidate, runKey } from "../../api/live";
import { usePopup } from "../../app/popup";
import { hasReason, reasonOf } from "../../ui/Reason";
import { type Body, useAnswer } from "./answer";

/** One answer of a decision: a button at the top of its popup. */
export interface Answer {
  label: string;
  tone?: "primary" | "danger";
  /** Why it cannot be chosen now, shown beside it; the button is disabled. */
  disabled?: string | null;
  /** Sent on a click, with the comment as its reason. */
  body?: Body;
  /** Done before the body is sent, with the comment, such as clearing a breakpoint; a failure is shown and nothing is sent. */
  prepare?: (comment: string | null) => Promise<unknown>;
  /**
   * Or a value form opened under the buttons by a click (how many attempts, which step, a required reason): `send` answers;
   * `act` does something else that answers, such as submitting a session, and closes the popup when it is taken.
   */
  form?: (send: (body: Body) => void, busy: boolean, act: (fn: (comment: string | null) => Promise<unknown>) => void) => ComponentChildren;
}

interface AnswerBarProps {
  runId: string;
  wait: Schema<"WaitView">;
  /** Where the wait is and what it is, under the title: "#198 · verify › fix · intervention · opened 02:54". */
  context: string;
  answers: readonly Answer[];
  /** Beside the buttons: a summary, a tally, ticks. */
  aside?: ComponentChildren;
  /** Under the comment: what an answer does here, such as what Approve does when the engine may not merge. */
  note?: ComponentChildren;
  /** The answer whose value form is open at first, if any. */
  initiallyOpen?: number;
  /** The popup's body: why it waits, what each answer does, the evidence. */
  children?: ComponentChildren;
}

/**
 * A decision popup's frame (design-v2 s4.5): answer buttons and the comment at the top, a value form under the buttons when
 * an answer needs one, then the body. An answer the run takes closes the popup; a refusal is shown in place with its
 * errors (A10), and the popup stays. A wait that has closed, or that the caller may not answer, shows why and no buttons.
 */
export function AnswerBar({ runId, wait, context, answers, aside, note, initiallyOpen, children }: AnswerBarProps) {
  const answer = useAnswer(runId, wait.wait_id);
  const popups = usePopup();
  const [comment, setComment] = useState("");
  const [open, setOpen] = useState<number | null>(initiallyOpen ?? null);
  const [failed, setFailed] = useState<Error>();
  const [acting, setActing] = useState(false);

  const send = async (body: Body) => {
    setFailed(undefined);
    if (await answer.send({ ...body, reason: body.reason ?? reasonOf(comment) })) popups.close();
  };
  /** Runs what an answer does other than POST …/answers (an override, a session's submit); false when it was refused. */
  const run = async (fn: (comment: string | null) => Promise<unknown>): Promise<boolean> => {
    setFailed(undefined);
    setActing(true);
    try {
      await fn(reasonOf(comment));
      return true;
    } catch (e) {
      setFailed(e instanceof Error ? e : new Error(String(e)));
      return false;
    } finally {
      setActing(false);
    }
  };
  const act = async (fn: (comment: string | null) => Promise<unknown>) => {
    if (await run(fn)) {
      void invalidate([runKey(runId), "attention"]);
      popups.close();
    }
  };
  const choose = async (a: Answer) => {
    if (a.prepare && !(await run(a.prepare))) return;
    if (a.body) await send(a.body);
  };
  const busy = answer.busy || acting;
  const blocked = !wait.open ? "This decision is closed." : !wait.can_answer ? "You may not answer this." : null;
  const form = open === null ? undefined : answers[open]?.form;
  const error = failed ?? answer.error;
  const errors = error instanceof ApiError ? error.errors : [];

  return (
    <div class="ff-dec" data-wait={wait.wait_id}>
      <div class="ff-dec-top">
        <p class="ff-dec-context">
          <span class="ff-dec-hint">Click outside to close without answering</span>
          {context}
        </p>
        {blocked !== null ? (
          <p class="ff-dec-blocked">{blocked}</p>
        ) : (
          <>
            <div class="ff-dec-answers" role="group" aria-label="Answers">
              {answers.map((a, i) => (
                <button
                  key={a.label}
                  type="button"
                  class={`ff-btn${a.tone === "primary" ? " ff-btn-primary" : a.tone === "danger" ? " ff-btn-danger" : ""}`}
                  aria-expanded={a.form ? open === i : undefined}
                  disabled={!!a.disabled || busy}
                  title={a.disabled ?? undefined}
                  onClick={() => (a.form ? setOpen(o => (o === i ? null : i)) : void choose(a))}
                >
                  {a.label}
                </button>
              ))}
              {aside && <span class="ff-dec-aside">{aside}</span>}
            </div>
            {form && <div class="ff-dec-param">{form(body => void send(body), busy, fn => void act(fn))}</div>}
            <div class="ff-dec-comment">
              <label for={`comment-${wait.wait_id}`}>Comment</label>
              <input
                id={`comment-${wait.wait_id}`}
                value={comment}
                placeholder="optional, recorded with your answer"
                onInput={e => setComment(e.currentTarget.value)}
              />
            </div>
          </>
        )}
        {note && <div class="ff-dec-note">{note}</div>}
        {answer.pending !== undefined && <p class="ff-dec-pending">Recorded: {answer.pending}</p>}
        {error && (
          <div class="ff-dec-refused" role="alert" aria-label="Refused">
            <p>{error.message}</p>
            {errors.length > 0 && (
              <ul>
                {errors.map(e => (
                  <li key={e}>{e}</li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
      <div class="ff-dec-body">{children}</div>
    </div>
  );
}

/**
 * A value form for an answer that needs a reason (Reject…, End the run…, Escalate instead…): what it does, the reason
 * (required), and the answer's own button, disabled until there is a reason.
 */
export function ReasonForm({ does, verb, busy, onSend, children }: {
  does: string;
  verb: string;
  busy: boolean;
  onSend: (reason: string) => void;
  children?: ComponentChildren;
}) {
  const [reason, setReason] = useState("");
  return (
    <>
      <span>{does}</span>
      {children}
      <label class="ff-dec-inline">
        Reason (required)
        <input value={reason} onInput={e => setReason(e.currentTarget.value)} />
      </label>
      <button type="button" class="ff-btn ff-btn-danger" disabled={busy || !hasReason(reason)} onClick={() => onSend(reason)}>
        {verb}
      </button>
    </>
  );
}

/** "approve_spec" → "approve spec"; "verify.fix.person_triage" → "verify › fix › person triage". */
export const stepPath = (stepId: string): string => stepId.split(".").map(part => part.replaceAll("_", " ")).join(" › ");

/** A time as the mock-ups show it: "02:54". */
export const clock = (at: string): string => new Date(at).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
