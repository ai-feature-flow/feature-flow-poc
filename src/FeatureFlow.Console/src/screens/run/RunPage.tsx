import { api, ApiError, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Status } from "../../ui/Status";
import { ArtifactList } from "../artifacts/ArtifactList";
import { PlanPanel } from "../plan/PlanPanel";
import { Abort } from "./Abort";
import { Children } from "./Children";
import { History } from "./History";
import { Summary } from "./Summary";
import { Timeline } from "./Timeline";
import { ClosedWaits, OpenWaits } from "./Waits";
import "./RunPage.css";

/** The engine's run statuses while it still runs (Interpreter/Types.cs, RunStatus); any other is an end. */
const active: readonly string[] = ["starting", "running"];

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
 * A run's page (#/runs/{id}): its summary, open waits (a form for each decision, a banner for each status wait), closed
 * waits with their answers, the plan, child runs, history, timeline and artifacts. Abort is offered when the caller may.
 */
export function RunPage({ runId }: { runId: string }) {
  const detail = useLive([runKey(runId)], () => read(api.GET("/runs/{id}", { params: { path: { id: runId } } })));
  const plan = useLive([runKey(runId)], () => planOf(runId));
  if (detail.data === undefined) return detail.error ? <ErrorBox error={detail.error} /> : <Busy />;
  const { run, waits, timeline, children, answers, overrides, overrides_allowed } = detail.data;
  const planView = plan.data?.plan ?? null;
  // A run that has ended takes no abort or steering: the API answers 409.
  const allowed = active.includes(run.status) ? overrides_allowed : [];

  return (
    <article class="ff-run">
      <h1 class="ff-run-heading">
        <span class="ff-run-id">{run.run_id}</span> <Status status={run.status} />
      </h1>
      <p class="ff-run-template">{run.template}</p>
      {detail.error && <ErrorBox error={detail.error} />}
      <Summary run={run} />
      {allowed.includes("abort") && <Abort runId={run.run_id} />}
      <OpenWaits runId={run.run_id} waits={waits} plan={planView} />
      <ClosedWaits waits={waits} />
      <PlanPanel runId={run.run_id} plan={planView} note={plan.data?.note ?? plan.error?.message} overridesAllowed={allowed} />
      {children.length > 0 && <Children children={children} />}
      <History answers={answers} overrides={overrides} />
      <Timeline entries={timeline} />
      <ArtifactList runId={run.run_id} />
    </article>
  );
}
