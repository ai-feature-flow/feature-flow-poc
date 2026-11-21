import { useEffect, useRef } from "preact/hooks";
import type { Schema } from "../../api/client";
import { Age } from "../../ui/Age";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";
import { WaitForm } from "../decision/WaitForm";
import { progress } from "../needs-me/NeedsMe";

type Wait = Schema<"WaitView">;

/** The engine's status waits: shown as a status, never answered with a form (M8 follow-up 11). */
const statusWaits: readonly string[] = ["stalled", "stuck"];

/**
 * The run's open waits: a form for each decision, and a banner for each status wait. The page scrolls here when it opens
 * with a wait open, as a link from Needs me expects.
 */
export function OpenWaits({ runId, waits, plan }: { runId: string; waits: readonly Wait[]; plan: Schema<"PlanView"> | null }) {
  const section = useRef<HTMLElement>(null);
  const open = waits.filter(w => w.open);
  const any = open.length > 0;
  useEffect(() => {
    if (any) section.current?.scrollIntoView({ block: "start" });
  }, [runId]);

  return (
    <section ref={section} class="ff-run-section" aria-label="Open waits">
      <h2>Open waits</h2>
      {!any && <p>Nothing is waiting.</p>}
      {open.map(w =>
        statusWaits.includes(w.kind) ? (
          <p key={w.wait_id} class="ff-run-banner" role="status" data-wait={w.wait_id}>
            <Status status={w.kind} /> at {w.step_id}: <span class="ff-run-text">{w.prompt}</span> <Age at={w.updated_at} />
          </p>
        ) : (
          <WaitForm key={w.wait_id} runId={runId} wait={w} plan={plan} canAnswer={w.can_answer} />
        ),
      )}
    </section>
  );
}

/** The run's closed waits, each with the answer that closed it. */
export function ClosedWaits({ waits }: { waits: readonly Wait[] }) {
  const closed = waits.filter(w => !w.open);
  return (
    <section class="ff-run-section" aria-label="Closed waits">
      <h2>Closed waits</h2>
      {closed.length === 0 ? (
        <p>None yet.</p>
      ) : (
        <Table head={["Wait", "Step", "Answered by", "Choice", "Reason", "When"]}>
          {closed.map(w => (
            <tr key={w.wait_id} data-wait={w.wait_id}>
              <td>
                {w.kind}: {w.decision_type}
                {w.min_count > 1 && w.progress !== null && <div>{progress(w)}</div>}
              </td>
              <td>{w.step_id}</td>
              {w.answer === null ? (
                <td colSpan={3}>closed without an answer</td>
              ) : (
                <>
                  <td>{w.answer.actor}</td>
                  <td>{w.answer.choice}</td>
                  <td class="ff-run-text">{w.answer.reason ?? ""}</td>
                </>
              )}
              <td>
                <Age at={w.answer?.at ?? w.updated_at} />
              </td>
            </tr>
          ))}
        </Table>
      )}
    </section>
  );
}
