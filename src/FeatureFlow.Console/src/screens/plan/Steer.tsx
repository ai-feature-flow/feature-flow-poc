import { useEffect, useRef, useState } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { invalidate, runKey } from "../../api/live";
import { ErrorBox } from "../../ui/ErrorBox";
import { Reason, reasonOf } from "../../ui/Reason";

/**
 * An override chosen in the plan panel: its key (docs/05 section 5), the step it applies to (none for an amendment), and
 * for a breakpoint whether to set or clear it.
 */
export interface Choice {
  key: "breakpoint" | "skip" | "grant_attempts" | "amend";
  step?: Schema<"PlanStepView">;
  set?: boolean;
}

/** What a choice says it does, as the form's heading. */
export function titleOf({ key, step, set }: Choice): string {
  switch (key) {
    case "breakpoint":
      return `${set ? "Set" : "Clear"} breakpoint on ${step?.id}`;
    case "skip":
      return `Skip ${step?.id}`;
    case "grant_attempts":
      return `Grant attempts to ${step?.id}`;
    case "amend":
      return "Amend the plan";
  }
}

/** The outcomes a step may be skipped with: all but attempts_exhausted (Steering.cs, SkipError). */
export const outcomesOf = (step: Schema<"PlanStepView">): string[] =>
  step.outcomes.map(o => o.name).filter(name => name !== "attempts_exhausted");

/** A patch to start an amendment from, against the current revision (docs/03 section 10.1). */
const skeleton = (revision: number): string =>
  `api_version: feature-flow/v1\nkind: patch\nbase_revision: ${revision}\nresume_at:\nchanges:\n`;

/** The choice's argument as it starts. */
const initial = (choice: Choice, revision: number): string =>
  choice.key === "skip" && choice.step ? (outcomesOf(choice.step)[0] ?? "") : choice.key === "amend" ? skeleton(revision) : "";

/** The override's arguments by name (Steering.cs, OverrideError), or null while the argument is not valid. */
function changeOf(key: Choice["key"], argument: string, set?: boolean): Record<string, string> | null {
  switch (key) {
    case "breakpoint":
      return { set: String(set) };
    case "skip":
      return argument ? { outcome: argument } : null;
    case "grant_attempts":
      return /^[1-9]\d*$/.test(argument) ? { attempts: argument } : null;
    case "amend":
      return argument.trim() ? { patch: argument } : null;
  }
}

/** One override's form: its argument, an optional comment, and Apply (POST /runs/{id}/overrides). */
export function Steer({ runId, revision, choice, onDone, onCancel }: {
  runId: string;
  revision: number;
  choice: Choice;
  onDone: (result: string) => void;
  onCancel: () => void;
}) {
  const [argument, setArgument] = useState(() => initial(choice, revision));
  const [reason, setReason] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<Error>();
  const change = changeOf(choice.key, argument, choice.set);
  const form = useRef<HTMLFormElement>(null);
  // The form opens where it was chosen; bring it into view with its first field ready.
  useEffect(() => {
    form.current?.scrollIntoView({ block: "nearest" });
    form.current?.querySelector<HTMLElement>("select, input, textarea")?.focus();
  }, []);

  const apply = async (e: Event) => {
    e.preventDefault();
    if (change === null) return;
    setSending(true);
    setError(undefined);
    try {
      const reply = await read(
        api.POST("/runs/{id}/overrides", {
          params: { path: { id: runId } },
          body: { key: choice.key, reason: reasonOf(reason), step_id: choice.step?.id ?? null, change },
        }),
      );
      invalidate([runKey(runId)]);
      onDone(reply.result);
    } catch (failure) {
      setError(failure instanceof Error ? failure : new Error(String(failure)));
    } finally {
      setSending(false);
    }
  };

  return (
    <form ref={form} class="ff-steer" onSubmit={apply}>
      <h3>{titleOf(choice)}</h3>
      {choice.key === "skip" && (
        <label>
          Outcome
          <select value={argument} onChange={e => setArgument(e.currentTarget.value)}>
            {choice.step &&
              outcomesOf(choice.step).map(name => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
          </select>
        </label>
      )}
      {choice.key === "grant_attempts" && (
        <label>
          Attempts
          <input type="number" min={1} step={1} value={argument} onInput={e => setArgument(e.currentTarget.value)} />
        </label>
      )}
      {choice.key === "amend" && (
        <label>
          Patch
          <textarea rows={8} value={argument} onInput={e => setArgument(e.currentTarget.value)} />
        </label>
      )}
      <Reason value={reason} onInput={setReason} />
      {error && <ErrorBox error={error} />}
      <p>
        <button type="submit" disabled={sending || change === null}>
          Apply
        </button>{" "}
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
      </p>
    </form>
  );
}
