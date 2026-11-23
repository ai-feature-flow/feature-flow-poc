import { useEffect, useId, useRef, useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { usePopup } from "../../app/popup";
import { ErrorBox } from "../../ui/ErrorBox";
import { hasReason } from "../../ui/Reason";
import { override } from "./override";

type Step = Schema<"PlanStepView">;

/** A steer chosen on a step (design-v2 s4.4): Steer ▾'s pause before (or clear it), skip, grant attempts; Edit step's amend and change role. */
export type SteerKind = "breakpoint" | "skip" | "grant_attempts" | "edit";

/** The outcomes a step may be skipped with: all but attempts_exhausted (Steering.cs, SkipError). */
export const outcomesOf = (step: Step): string[] => step.outcomes.map(o => o.name).filter(name => name !== "attempts_exhausted");

/** What Edit step changes for the next attempt: the step's parameters or limits (an `amend` patch's `set`), or its role (`change_role`). */
type Field = "with" | "limits" | "role";

/** Text indented under a YAML key. */
const indent = (text: string, by: string) =>
  text
    .split("\n")
    .filter(line => line.trim() !== "")
    .map(line => by + line)
    .join("\n");

/** The amend patch that sets one field of the step (docs/03 section 10.2, `set`), against the current revision. */
export function setPatch(revision: number, stepId: string, field: "with" | "limits", value: string): string {
  return [
    "api_version: feature-flow/v1",
    "kind: patch",
    `base_revision: ${revision}`,
    "changes:",
    "  - op: set",
    `    target: ${stepId}`,
    `    field: ${field}`,
    "    value:",
    indent(value, "      "),
    "",
  ].join("\n");
}

interface Prepared {
  /** The override to send, or null while the form is not complete. */
  request: Omit<Schema<"OverrideRequest">, "reason"> | null;
  /** What it will do, said before it is done. */
  consequence: string;
}

function prepare(kind: SteerKind, step: Step, plan: Schema<"PlanView">, value: string, field: Field): Prepared {
  switch (kind) {
    case "breakpoint":
      return step.breakpoint
        ? {
            request: { key: "breakpoint", step_id: step.id, change: { set: "false" } },
            consequence: `The run no longer pauses before ${step.id}.`,
          }
        : {
            request: { key: "breakpoint", step_id: step.id, change: { set: "true" } },
            consequence: `The run pauses before ${step.id} starts, and waits for you; Continue there lets it start.`,
          };
    case "skip": {
      const route = step.outcomes.find(o => o.name === value)?.route;
      return {
        request: value ? { key: "skip", step_id: step.id, change: { outcome: value } } : null,
        consequence: `${step.id} will not run: it is recorded as skipped, ending ${value || "…"}${route ? `, and the run goes on to ${route}` : ""}.`,
      };
    }
    case "grant_attempts": {
      const n = /^[1-9]\d*$/.test(value) ? Number(value) : null;
      const allowed = step.attempts_allowed ?? null;
      const used = step.attempts_used ?? 0;
      return {
        request: n === null ? null : { key: "grant_attempts", step_id: step.id, change: { attempts: String(n) } },
        consequence:
          n === null
            ? "Give how many attempts to add."
            : `${step.id} may use ${n} more attempt${n === 1 ? "" : "s"}${allowed !== null ? `: ${used} of ${allowed} used, ${allowed + n} allowed after this` : ""}.`,
      };
    }
    case "edit": {
      if (field === "role")
        return {
          request: value.trim() ? { key: "change_role", step_id: step.id, change: { role: value.trim() } } : null,
          consequence: `From its next attempt, ${step.id} runs as ${value.trim() || "…"}; the plan becomes revision ${plan.revision + 1}.`,
        };
      const now = step[field] ?? "";
      const name = field === "with" ? "parameters (with)" : "limits";
      return {
        request: value.trim() && value.trim() !== now.trim() ? { key: "amend", step_id: null, change: { patch: setPatch(plan.revision, step.id, field, value) } } : null,
        consequence:
          value.trim() === now.trim()
            ? `Change ${step.id}'s ${name} below; they are shown as the plan has them now.`
            : `From its next attempt, ${step.id}'s ${name} become exactly what is below: a key left out is removed. The plan becomes revision ${plan.revision + 1}. This run only.`,
      };
    }
  }
}

export const titleOf = (kind: SteerKind, step: Step): string =>
  kind === "breakpoint"
    ? step.breakpoint
      ? `Clear the breakpoint on ${step.id}`
      : `Pause before ${step.id}`
    : kind === "skip"
      ? `Skip ${step.id}`
      : kind === "grant_attempts"
        ? `Grant attempts to ${step.id}`
        : `Edit ${step.id}`;

/**
 * One steer on a step, in a popup (design-v2 s4.4): its value, a preview of its consequence, and a reason, which every
 * steer needs here (Q4: steering changes this run only, with a recorded reason). Applied with POST /runs/{id}/overrides;
 * the engine's refusal is shown in place.
 */
export function SteerForm({ runId, plan, step, kind, roles }: { runId: string; plan: Schema<"PlanView">; step: Step; kind: SteerKind; roles: boolean }) {
  const popups = usePopup();
  const [value, setValue] = useState(kind === "skip" ? (outcomesOf(step)[0] ?? "") : kind === "grant_attempts" ? "1" : kind === "edit" ? (step.with ?? "") : "");
  const [field, setField] = useState<Field>("with");
  const [reason, setReason] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<Error>();
  const first = useRef<HTMLFormElement>(null);
  const ids = { value: useId(), reason: useId(), field: useId() };
  const prepared = prepare(kind, step, plan, value, field);
  useEffect(() => first.current?.querySelector<HTMLElement>("select, input, textarea")?.focus(), []);

  const apply = async (e: Event) => {
    e.preventDefault();
    if (prepared.request === null || !hasReason(reason)) return;
    setSending(true);
    setError(undefined);
    try {
      await override(runId, { ...prepared.request, reason: reason.trim() });
      popups.close();
    } catch (failure) {
      setError(failure instanceof Error ? failure : new Error(String(failure)));
    } finally {
      setSending(false);
    }
  };

  return (
    <form ref={first} class="ff-steer" onSubmit={apply}>
      {kind === "skip" && (
        <div class="ff-steer-field">
          <label for={ids.value}>Outcome</label>
          <select id={ids.value} value={value} onChange={e => setValue(e.currentTarget.value)}>
            {outcomesOf(step).map(name => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </div>
      )}
      {kind === "grant_attempts" && (
        <div class="ff-steer-field">
          <label for={ids.value}>Attempts</label>
          <input id={ids.value} type="number" min={1} step={1} value={value} onInput={e => setValue(e.currentTarget.value)} />
        </div>
      )}
      {kind === "edit" && (
        <>
          <div class="ff-steer-field">
            <label for={ids.field}>Change</label>
            <select
              id={ids.field}
              value={field}
              onChange={e => {
                const next = e.currentTarget.value as Field;
                setField(next);
                setValue(next === "role" ? "" : (step[next] ?? ""));
              }}
            >
              <option value="with">Parameters (with)</option>
              <option value="limits">Limits</option>
              {roles && <option value="role">Role</option>}
              <option value="prompt" disabled>
                Prompt: set by the step's role and task; an amend cannot change it (needs engine work, B2)
              </option>
            </select>
          </div>
          <div class="ff-steer-field">
            <label for={ids.value}>{field === "role" ? "Role" : field === "with" ? "Parameters, as YAML" : "Limits, as YAML"}</label>
            {field === "role" ? (
              <input id={ids.value} value={value} placeholder="std:roles/implementer" onInput={e => setValue(e.currentTarget.value)} />
            ) : (
              <textarea
                id={ids.value}
                rows={5}
                class="ff-mono"
                value={value}
                placeholder={field === "with" ? "ticket_id: FF-1042" : "attempts: 4"}
                onInput={e => setValue(e.currentTarget.value)}
              />
            )}
          </div>
        </>
      )}
      <p class="ff-steer-consequence" role="note" aria-label="Consequence">
        {prepared.consequence}
      </p>
      {kind === "edit" && field !== "role" && prepared.request?.change?.patch && (
        <div class="ff-steer-diff" aria-label="Before and after">
          <div>
            <div class="ff-k">Now</div>
            <pre class="ff-dec-yaml" aria-label="Now">
              {step[field] ?? "(none)"}
            </pre>
          </div>
          <div>
            <div class="ff-k">After</div>
            <pre class="ff-dec-yaml" aria-label="After">
              {value.trim()}
            </pre>
          </div>
        </div>
      )}
      {kind === "edit" && field !== "role" && prepared.request?.change?.patch && (
        <pre class="ff-dec-yaml" aria-label="Patch">
          {prepared.request.change.patch}
        </pre>
      )}
      <div class="ff-steer-field">
        <label for={ids.reason}>Reason (required)</label>
        <textarea id={ids.reason} rows={2} value={reason} placeholder="recorded with the change, and shown to the next agent" onInput={e => setReason(e.currentTarget.value)} />
      </div>
      {error && <ErrorBox error={error} />}
      <p>
        <button type="submit" class="ff-btn ff-btn-primary" disabled={sending || prepared.request === null || !hasReason(reason)}>
          Apply
        </button>{" "}
        <button type="button" class="ff-btn ff-btn-ghost" onClick={() => popups.close()}>
          Cancel
        </button>
      </p>
    </form>
  );
}
