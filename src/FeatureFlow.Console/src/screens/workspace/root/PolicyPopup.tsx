import { api, read } from "../../../api/client";
import { runKey, useLive } from "../../../api/live";
import { Busy } from "../../../ui/Busy";
import { ErrorBox } from "../../../ui/ErrorBox";
import { Table } from "../../../ui/Table";

const sources: Record<string, string> = { organisation: "organisation policy", library: "the library", default: "the default" };

/** A limit, or why it is not shown. */
const limit = (value: number | null | undefined): string => (value === null || value === undefined ? "set by the engine" : String(value));

/**
 * Policy in force (design-v2 s4.4, Run actions), from `GET /runs/{id}/policy`: who answers each decision type of the run's
 * plan and how many must, whether it may be answered automatically and where the rule comes from; who may use each override,
 * and whether you may; and the attempt limits. Text only.
 */
export function PolicyPopup({ runId }: { runId: string }) {
  const policy = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/policy", { params: { path: { id: runId } } })));
  const data = policy.data;
  if (!data) return policy.error ? <ErrorBox error={policy.error} /> : <Busy />;
  return (
    <div class="ff-policy">
      {policy.error && <ErrorBox error={policy.error} />}
      <h3>Decisions</h3>
      {data.decisions.length === 0 ? (
        <p class="ff-muted">The run's plan has no decisions.</p>
      ) : (
        <Table head={["Decision", "Steps", "Who answers", "Automatic", "Rule from"]}>
          {data.decisions.map(d => (
            <tr key={d.decision_type} data-decision={d.decision_type}>
              <td class="ff-mono">{d.decision_type}</td>
              <td class="ff-mono">
                {d.step_ids.map(s => (
                  <div key={s}>{s}</div>
                ))}
              </td>
              <td>
                {d.roles.join(", ")}
                {d.min_count > 1 && <div class="ff-muted">{d.min_count} people</div>}
              </td>
              <td>{d.automatic ? "allowed" : "no"}</td>
              <td>{sources[d.source] ?? d.source}</td>
            </tr>
          ))}
        </Table>
      )}
      <h3>Overrides</h3>
      <Table head={["Override", "Who may", "You"]}>
        {data.overrides.map(o => (
          <tr key={o.key} data-override={o.key}>
            <td class="ff-mono">{o.key}</td>
            <td>{o.roles.join(", ")}</td>
            <td>{o.yours ? "may" : "may not"}</td>
          </tr>
        ))}
      </Table>
      <h3>Attempts</h3>
      <Table head={["Limit", "Value"]}>
        <tr>
          <td>A step's default attempts</td>
          <td>{limit(data.attempts.default_attempts)}</td>
        </tr>
        <tr>
          <td>The run's ceiling</td>
          <td>{limit(data.attempts.run_ceiling)}</td>
        </tr>
        <tr>
          <td>Hard ceiling per step</td>
          <td>{limit(data.attempts.hard_step_ceiling)}</td>
        </tr>
        <tr>
          <td>Hard ceiling per run</td>
          <td>{limit(data.attempts.hard_run_ceiling)}</td>
        </tr>
      </Table>
    </div>
  );
}
