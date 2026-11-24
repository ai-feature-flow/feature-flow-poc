import type { ComponentChildren } from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { usePopup } from "../../app/popup";
import { ErrorBox } from "../../ui/ErrorBox";
import { hasReason } from "../../ui/Reason";
import { StepPicker } from "../decision/StepPicker";
import { override } from "./override";

type Step = Schema<"PlanStepView">;
type Request = Omit<Schema<"OverrideRequest">, "reason">;

/**
 * A steer chosen on a step (design-v2 s4.4): Steer ▾'s pause before (or clear it), skip, grant attempts, go to a step and
 * re-plan; Edit step's amend, change role and instructions for the next attempt.
 */
export type SteerKind = "breakpoint" | "skip" | "grant_attempts" | "go_to" | "replan" | "edit";

/** The outcomes a step may be skipped with: all but attempts_exhausted (Steering.cs, SkipError). */
export const outcomesOf = (step: Step): string[] => step.outcomes.map(o => o.name).filter(name => name !== "attempts_exhausted");

/**
 * What Edit step changes for the next attempt: the step's parameters or limits (an `amend` patch's `set`), its role
 * (`change_role`), or a person's instructions added to its next prompt (`instruct`, build-console-b2 plan s1.3).
 */
type Field = "with" | "limits" | "role" | "instructions";

/** The most an instruction may hold, in UTF-8 bytes; the API refuses a longer one. */
const instructionLimit = 4096;

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
  request: Request | null;
  /** What it will do, said before it is done. */
  consequence: string;
}

function prepare(kind: "breakpoint" | "skip" | "grant_attempts" | "edit", step: Step, plan: Schema<"PlanView">, value: string, field: Field): Prepared {
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
      if (field === "instructions") {
        const bytes = new TextEncoder().encode(value.trim()).length;
        return {
          // The API adds instruct_protected for a protected step when the caller holds it; the key sent is instruct.
          request: value.trim() && bytes <= instructionLimit ? { key: "instruct", step_id: step.id, change: { text: value.trim() } } : null,
          consequence:
            bytes > instructionLimit
              ? `Instructions may hold at most ${instructionLimit} bytes; these hold ${bytes}.`
              : `From its next attempt, ${step.id}'s prompt includes your instructions, until the step next succeeds. The plan does not change. This run only.`,
        };
      }
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
        : kind === "go_to"
          ? "Go to a step"
          : kind === "replan"
            ? "Re-plan"
            : `Edit ${step.id}`;

/**
 * The common part of every steer form: the consequence, a required reason, the engine's refusal in place, Apply and Cancel.
 * `onApplied` receives the engine's reply; without it the popup closes.
 */
function Steer({ runId, request, consequence, onApplied, children }: {
  runId: string;
  request: Request | null;
  consequence: ComponentChildren;
  onApplied?: (result: string) => void;
  children: ComponentChildren;
}) {
  const popups = usePopup();
  const [reason, setReason] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<Error>();
  const first = useRef<HTMLFormElement>(null);
  const id = useId();
  useEffect(() => first.current?.querySelector<HTMLElement>("select, input, textarea")?.focus(), []);

  const apply = async (e: Event) => {
    e.preventDefault();
    if (request === null || !hasReason(reason)) return;
    setSending(true);
    setError(undefined);
    try {
      const result = await override(runId, { ...request, reason: reason.trim() });
      if (onApplied) onApplied(result);
      else popups.close();
    } catch (failure) {
      setError(failure instanceof Error ? failure : new Error(String(failure)));
    } finally {
      setSending(false);
    }
  };

  return (
    <form ref={first} class="ff-steer" onSubmit={apply}>
      {children}
      <div class="ff-steer-consequence" role="note" aria-label="Consequence">
        {consequence}
      </div>
      <div class="ff-steer-field">
        <label for={id}>Reason (required)</label>
        <textarea id={id} rows={2} value={reason} placeholder="recorded with the change, and shown to the next agent" onInput={e => setReason(e.currentTarget.value)} />
      </div>
      {error && <ErrorBox error={error} />}
      <p>
        <button type="submit" class="ff-btn ff-btn-primary" disabled={sending || request === null || !hasReason(reason)}>
          Apply
        </button>{" "}
        <button type="button" class="ff-btn ff-btn-ghost" onClick={() => popups.close()}>
          Cancel
        </button>
      </p>
    </form>
  );
}

/** A step the core would cancel on a go to: in flight (or to be resumed or retried), or waiting. */
const inFlight = (status: string): boolean => status === "running" || status === "retrying" || status === "interrupted" || status.startsWith("waiting(");

/** What a cancelled step was doing, in words. */
const doingOf = (status: string): string => (status.startsWith("waiting(") ? `waiting: ${status.slice(8, -1).replaceAll("_", " ")}` : status);

/**
 * Go to a step or re-plan on a live run (build-console-b2 plan s1.2): the grouped step picker the intervention uses, clean
 * up, and the consequence, listing from the current plan every step that is running or waiting, which the core cancels
 * (with a child run it started) before the run continues at the target. The engine's reply, naming what it cancelled, is
 * shown after Apply. Re-plan offers only agent steps; the engine refuses one without planning authority.
 */
function MoveForm({ runId, plan: opened, step, kind }: { runId: string; plan: Schema<"PlanView">; step: Step; kind: "go_to" | "replan" }) {
  const popups = usePopup();
  const live = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/plan", { params: { path: { id: runId } } })));
  const plan = live.data ?? opened;
  const candidates = kind === "replan" ? { ...plan, steps: plan.steps.filter(s => s.form === "agent") } : plan;
  const [target, setTarget] = useState(
    candidates.steps.some(s => s.id === step.id) ? step.id : (candidates.steps.find(s => s.id.split(".").pop() === "plan")?.id ?? candidates.steps[0]?.id ?? ""),
  );
  const [cleanUp, setCleanUp] = useState(false);
  const [result, setResult] = useState<string>();
  const ids = { target: useId(), cleanUp: useId() };
  const chosen = plan.steps.find(s => s.id === target);
  const decision = chosen?.form === "decision";
  const cancelled = plan.steps.filter(s => inFlight(s.status));

  if (result !== undefined)
    return (
      <div class="ff-steer">
        <p role="status" class="ff-text">
          {result}
        </p>
        <p>
          <button type="button" class="ff-btn ff-btn-primary" onClick={() => popups.close()}>
            Done
          </button>
        </p>
      </div>
    );

  return (
    <Steer
      runId={runId}
      request={target ? { key: kind, step_id: target, change: { clean_up: cleanUp && !decision ? "true" : "false" } } : null}
      onApplied={setResult}
      consequence={
        <>
          <p>
            {kind === "go_to" ? `The run continues at ${target || "…"}` : `The run goes back to ${target || "…"} to plan again`}
            {cleanUp && !decision ? ", after the branch is reset to where that step started" : ""}. Your reason is given to it as why it runs. This run only.
          </p>
          {cancelled.length === 0 ? (
            <p>Nothing is running or waiting, so nothing is cancelled.</p>
          ) : (
            <>
              <p>
                Cancelled first ({cancelled.length}), with any open question or decision withdrawn:
              </p>
              <ul aria-label="Cancelled">
                {cancelled.map(s => (
                  <li key={s.id}>
                    <span class="ff-mono">{s.id}</span> ({doingOf(s.status)}){s.child_run_id ? <>, and its child run <span class="ff-mono">{s.child_run_id}</span></> : null}
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      }
    >
      <div class="ff-steer-field">
        <label for={ids.target}>{kind === "go_to" ? "Continue from" : "Re-plan from"}</label>
        <StepPicker id={ids.target} plan={candidates} value={target} onChange={setTarget} />
      </div>
      <label class="ff-steer-check" for={ids.cleanUp}>
        <input id={ids.cleanUp} type="checkbox" checked={cleanUp && !decision} disabled={decision} onChange={e => setCleanUp(e.currentTarget.checked)} /> Clean up: reset
        the branch first{decision ? " (not at a decision, which touches no repository)" : ""}
      </label>
    </Steer>
  );
}

/** Where a person's instructions go in the step's next prompt (plan s1.3; RunContexts.RenderInstructions), around the text given. */
function PromptPreview({ text }: { text: string }) {
  return (
    <div class="ff-steer-field">
      <span class="ff-k">Where it goes in the prompt</span>
      <pre class="ff-dec-yaml ff-steer-prompt" aria-label="Where it goes in the prompt">
        <span class="ff-muted">{"… the reporting preamble, ## This step, and ## Why this step runs again when it does\n\n"}</span>
        {"## A person's instructions for this step\n\n"}
        {/* As RunContexts.RenderInstructions writes each: a bullet naming who gave it, later lines indented. */}
        {`- from you: ${(text.trim() || "(your instructions)").replaceAll("\n", "\n  ")}\n\n`}
        <span class="ff-muted">{"… the run context, and the step's task"}</span>
      </pre>
    </div>
  );
}

/**
 * One steer on a step, in a popup (design-v2 s4.4): its value, a preview of its consequence, and a reason, which every
 * steer needs here (Q4: steering changes this run only, with a recorded reason). Applied with POST /runs/{id}/overrides;
 * the engine's refusal is shown in place. `roles` says whether Edit step offers a role change.
 */
export function SteerForm({ runId, plan, step, kind, roles, instruct }: {
  runId: string;
  plan: Schema<"PlanView">;
  step: Step;
  kind: SteerKind;
  roles: boolean;
  /** Why Edit step cannot add instructions, or null when it can. */
  instruct: string | null;
}) {
  if (kind === "go_to" || kind === "replan") return <MoveForm runId={runId} plan={plan} step={step} kind={kind} />;
  return <ChangeForm runId={runId} plan={plan} step={step} kind={kind} roles={roles} instruct={instruct} />;
}

function ChangeForm({ runId, plan, step, kind, roles, instruct }: {
  runId: string;
  plan: Schema<"PlanView">;
  step: Step;
  kind: "breakpoint" | "skip" | "grant_attempts" | "edit";
  roles: boolean;
  instruct: string | null;
}) {
  const [value, setValue] = useState(kind === "skip" ? (outcomesOf(step)[0] ?? "") : kind === "grant_attempts" ? "1" : kind === "edit" ? (step.with ?? "") : "");
  const [field, setField] = useState<Field>("with");
  const ids = { value: useId(), field: useId() };
  const prepared = prepare(kind, step, plan, value, field);
  const label = field === "role" ? "Role" : field === "with" ? "Parameters, as YAML" : field === "limits" ? "Limits, as YAML" : "Instructions for the next attempt";

  return (
    <Steer runId={runId} request={prepared.request} consequence={prepared.consequence}>
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
                setValue(next === "role" || next === "instructions" ? "" : (step[next] ?? ""));
              }}
            >
              <option value="with">Parameters (with)</option>
              <option value="limits">Limits</option>
              {roles && <option value="role">Role</option>}
              <option value="instructions" disabled={instruct !== null}>
                {instruct === null ? "Instructions for the next attempt" : `Instructions for the next attempt: ${instruct}`}
              </option>
            </select>
          </div>
          {field === "instructions" && step.protected && (
            <p class="ff-steer-protected" role="note" aria-label="Protected step">
              {step.id} is protected. Instructions could weaken what it checks, so they need instruct_protected, which you hold; they are recorded with your name
              and reason.
            </p>
          )}
          <div class="ff-steer-field">
            <label for={ids.value}>{label}</label>
            {field === "role" ? (
              <input id={ids.value} value={value} placeholder="std:roles/implementer" onInput={e => setValue(e.currentTarget.value)} />
            ) : (
              <textarea
                id={ids.value}
                rows={field === "instructions" ? 6 : 5}
                class={field === "instructions" ? undefined : "ff-mono"}
                value={value}
                placeholder={field === "with" ? "ticket_id: FF-1042" : field === "limits" ? "attempts: 4" : "Keep the public API unchanged; add the index in a new migration."}
                onInput={e => setValue(e.currentTarget.value)}
              />
            )}
          </div>
          {field === "instructions" && <PromptPreview text={value} />}
        </>
      )}
      {kind === "edit" && (field === "with" || field === "limits") && prepared.request?.change?.patch && (
        <>
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
          <pre class="ff-dec-yaml" aria-label="Patch">
            {prepared.request.change.patch}
          </pre>
        </>
      )}
    </Steer>
  );
}
