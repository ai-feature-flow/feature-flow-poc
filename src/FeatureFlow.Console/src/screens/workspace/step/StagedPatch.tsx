import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../../api/client";
import { runKey, useLive } from "../../../api/live";
import { Age } from "../../../ui/Age";
import { ErrorBox } from "../../../ui/ErrorBox";
import { Fold } from "../../../ui/Fold";
import "../../decision/decision.css";
import "../../steer/steer.css";

const marks = { added: "+ added", removed: "− removed", changed: "~ changed" } as const;

const targets: Record<string, string> = { self: "this run's plan", parent: "the parent run's plan" };

/** One operation of a patch document: its op and the step it names. */
interface Operation {
  op: string;
  step: string;
}

/**
 * The patch's reason and operations, read from the document when it is JSON (as the agent tools stage it); null when it is
 * not, and then only the document itself is shown. Values are kept as text.
 */
function partsOf(document: string): { reason: string | null; operations: Operation[] } | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(document);
  } catch {
    return null;
  }
  if (typeof parsed !== "object" || parsed === null) return null;
  const doc = parsed as { metadata?: { reason?: unknown }; changes?: unknown };
  const text = (v: unknown) => (typeof v === "string" ? v : null);
  const operations = (Array.isArray(doc.changes) ? doc.changes : []).map((c: Record<string, unknown>): Operation => {
    const step = c.step as Record<string, unknown> | undefined;
    const named = text(step?.id) ?? text(c.target) ?? text(c.step_id) ?? "";
    const where = text(c.after) ? ` after ${c.after}` : text(c.before) ? ` before ${c.before}` : "";
    return { op: text(c.op) ?? "?", step: named + where };
  });
  return { reason: text(doc.metadata?.reason), operations };
}

/** The plan as the step's staged patch would make it (GET /runs/{id}/plan?staged={step}), read while shown. */
function StagedPlan({ runId, stepId, staged }: { runId: string; stepId: string; staged: Schema<"StagedPatchView"> }) {
  // Keyed by the staged patch too, so a patch staged again is read again.
  const plan = useLive([runKey(runId), `staged:${runId}/${stepId}/${staged.execution}/${staged.at}`], () =>
    read(api.GET("/runs/{id}/plan", { params: { path: { id: runId }, query: { staged: stepId } } })),
  );
  if (plan.error) return <ErrorBox error={plan.error} />;
  if (!plan.data) return <p class="ff-muted">Reading the plan as it would become…</p>;
  return (
    <table class="ff-dec-table ff-dec-plan" aria-label="Plan as it would become">
      <thead>
        <tr>
          <th>Step</th>
          <th>What it does</th>
          <th>Change</th>
        </tr>
      </thead>
      <tbody>
        {plan.data.steps.map(s => (
          <tr key={s.id} class={s.change ? `ff-dec-${s.change}` : undefined} data-change={s.change ?? "none"}>
            <td class="ff-mono">{s.id}</td>
            <td class="ff-dec-text">{s.what}</td>
            <td>{s.change ? marks[s.change] : ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * The plan patch a running step's agent has staged (A8), from the step detail's `staged_patch`: its target, revision,
 * reason and operations, the document folded, and on request the plan as it would become (`GET /runs/{id}/plan?staged=`,
 * only for a patch to this run's own plan). Staged is not proposed: the step proposes it when it finishes. The step page
 * renders this only while a patch is staged, so a withdrawn one disappears on the page's next read.
 */
export function StagedPatch({ runId, stepId, staged }: { runId: string; stepId: string; staged: Schema<"StagedPatchView"> }) {
  const [shown, setShown] = useState(false);
  const parts = partsOf(staged.patch);
  return (
    <section class="ff-box ff-staged" aria-label="Staged plan change">
      <header class="ff-pane-head">
        <h2>Staged plan change</h2>
        <span class="ff-muted ff-right-end">
          staged <Age at={staged.at} />
        </span>
      </header>
      <div class="ff-pane-body ff-steer">
        <p>The agent has staged this change. It is proposed when execution {staged.execution} finishes, and may change or be withdrawn before then.</p>
        <dl class="ff-dec-facts">
          <dt>Changes</dt>
          <dd>{targets[staged.target] ?? staged.target}</dd>
          <dt>Against revision</dt>
          <dd>{staged.revision}</dd>
          {parts?.reason && (
            <>
              <dt>Its reason</dt>
              <dd class="ff-dec-text">{parts.reason}</dd>
            </>
          )}
          {parts && (
            <>
              <dt>Operations</dt>
              <dd>
                <ul class="ff-staged-ops" aria-label="Operations">
                  {parts.operations.map((o, i) => (
                    <li key={i}>
                      <b>{o.op}</b> <span class="ff-mono">{o.step}</span>
                    </li>
                  ))}
                </ul>
              </dd>
            </>
          )}
        </dl>
        <Fold summary="The patch">
          <pre class="ff-dec-yaml" aria-label="Patch">
            {staged.patch}
          </pre>
        </Fold>
        {staged.target === "self" && (
          <p>
            <button type="button" class="ff-btn" aria-expanded={shown} onClick={() => setShown(s => !s)}>
              {shown ? "Hide the plan as it would become" : "Show the plan as it would become"}
            </button>
          </p>
        )}
        {shown && staged.target === "self" && <StagedPlan runId={runId} stepId={stepId} staged={staged} />}
      </div>
    </section>
  );
}
