import { api, read } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import { AnswerBar, ReasonForm } from "./AnswerBar";
import type { DecisionProps } from "./kinds";

const marks = { added: "+ added", removed: "− removed", changed: "~ changed" } as const;

/**
 * A patch approval (design-v2 s4.5): Approve · Reject…; who proposed it and why, why it needs a person, the plan as it
 * would become (GET /runs/{id}/plan?proposed=, A8) with each step marked added, removed or changed, the operations folded,
 * and where the run continues.
 */
export function PatchApproval({ runId, wait, context }: DecisionProps) {
  const proposed = useLive([runKey(runId)], () =>
    read(api.GET("/runs/{id}/plan", { params: { path: { id: runId }, query: { proposed: wait.wait_id } } })),
  );
  const patch = wait.patch ?? null;
  const plan = proposed.data ?? null;
  const changed = plan?.steps.filter(s => s.change) ?? [];

  return (
    <AnswerBar
      runId={runId}
      wait={wait}
      context={context}
      answers={[
        { label: "Approve", tone: "primary", body: { action: "approve" } },
        {
          label: "Reject…",
          tone: "danger",
          form: (send, busy) => (
            <ReasonForm does="The plan stays as it is; the proposer is told your reason." verb="Reject" busy={busy} onSend={reason => send({ action: "reject", reason })} />
          ),
        },
      ]}
      aside={plan && `${changed.length} step${changed.length === 1 ? "" : "s"} change`}
    >
      {patch === null ? (
        <p class="ff-dec-text">{wait.prompt}</p>
      ) : (
        <dl class="ff-dec-facts">
          <dt>Proposed by</dt>
          <dd>
            {patch.proposer_step_id ? `${patch.proposer_step_id} (${patch.proposer_type})` : patch.proposer_type}
            {patch.from_child ? ", in a child run" : ""}
          </dd>
          <dt>Its reason</dt>
          <dd class="ff-dec-text">{patch.reason ?? "none given"}</dd>
          <dt>Why it needs you</dt>
          <dd>
            {patch.needs_approval.map(n => (
              <div key={n}>{n}</div>
            ))}
          </dd>
          <dt>Continues at</dt>
          <dd>{patch.continues_at ?? "where the run was"}</dd>
          <dt>Against revision</dt>
          <dd>{patch.base_revision}</dd>
        </dl>
      )}
      <h3>The plan as it would become</h3>
      {proposed.error && <ErrorBox error={proposed.error} />}
      {plan === null && !proposed.error && <p class="ff-dec-muted">{proposed.loading ? "Reading the proposed plan…" : "No plan to show."}</p>}
      {plan !== null && (
        <table class="ff-dec-table ff-dec-plan" aria-label="Proposed plan">
          <thead>
            <tr>
              <th>Step</th>
              <th>What it does</th>
              <th>Change</th>
            </tr>
          </thead>
          <tbody>
            {plan.steps.map(s => (
              <tr key={s.id} class={s.change ? `ff-dec-${s.change}` : undefined} data-change={s.change ?? "none"}>
                <td class="ff-mono">{s.id}</td>
                <td class="ff-dec-text">{s.what}</td>
                <td>{s.change ? marks[s.change] : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {patch !== null && (
        <Fold summary="The operations">
          <pre class="ff-dec-yaml">{patch.document}</pre>
        </Fold>
      )}
    </AnswerBar>
  );
}
