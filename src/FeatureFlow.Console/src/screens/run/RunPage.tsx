import { api, ApiError, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Status } from "../../ui/Status";
import { ArtifactList } from "../artifacts/ArtifactList";
import { WaitForm } from "../decision/WaitForm";
import { PlanPanel } from "../plan/PlanPanel";
import "./RunPage.css";

/** The engine's status waits: shown as a status, never answered with a form (M8 follow-up 11). */
const statusWaits: readonly string[] = ["stalled", "stuck"];

/** The run's plan, or why there is none to show (404: not indexed yet; 409: it does not load). */
async function planOf(runId: string): Promise<{ plan: Schema<"PlanView"> | null; note?: string }> {
  try {
    return { plan: await read(api.GET("/runs/{id}/plan", { params: { path: { id: runId } } })) };
  } catch (e) {
    if (e instanceof ApiError && (e.status === 404 || e.status === 409)) return { plan: null, note: e.message };
    throw e;
  }
}

/**
 * A run's page (#/runs/{id}). Task T3 gives the heading and places the other tasks' parts: each open wait's form, the
 * plan panel and the artifact list. Task T4 fills the rest (summary, status banner, closed waits, history, children,
 * timeline, abort).
 */
export function RunPage({ runId }: { runId: string }) {
  const detail = useLive([runKey(runId)], () => read(api.GET("/runs/{id}", { params: { path: { id: runId } } })));
  const plan = useLive([runKey(runId)], () => planOf(runId));
  if (detail.data === undefined) return detail.error ? <ErrorBox error={detail.error} /> : <Busy />;
  const { run, waits, overrides_allowed } = detail.data;

  return (
    <article class="ff-run">
      <h1 class="ff-run-heading">
        <span class="ff-run-id">{run.run_id}</span> <Status status={run.status} />
      </h1>
      <p class="ff-run-template">{run.template}</p>
      {detail.error && <ErrorBox error={detail.error} />}
      {waits
        .filter(w => w.open && !statusWaits.includes(w.kind))
        .map(w => (
          <WaitForm key={w.wait_id} runId={run.run_id} wait={w} plan={plan.data?.plan ?? null} canAnswer={w.can_answer} />
        ))}
      <PlanPanel runId={run.run_id} plan={plan.data?.plan ?? null} note={plan.data?.note ?? plan.error?.message} overridesAllowed={overrides_allowed} />
      <ArtifactList runId={run.run_id} />
    </article>
  );
}
