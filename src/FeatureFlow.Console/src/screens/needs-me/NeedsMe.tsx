import type { Schema } from "../../api/client";
import { useAttention } from "../../app/attention";
import * as links from "../../app/links";
import { Age } from "../../ui/Age";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { progress } from "../../ui/progress";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";
import "./NeedsMe.css";

type Item = Schema<"AttentionItem">;

/** The items grouped by run, in the order the API gives them (oldest wait first). */
function byRun(items: readonly Item[]): Item[][] {
  const groups = new Map<string, Item[]>();
  for (const item of items) {
    const group = groups.get(item.run.run_id);
    if (group) group.push(item);
    else groups.set(item.run.run_id, [item]);
  }
  return [...groups.values()];
}

/** Needs me (#/): every open wait the caller may answer now (GET /attention), grouped by run. */
export function NeedsMe() {
  const attention = useAttention();
  if (attention.data === undefined) return attention.error ? <ErrorBox error={attention.error} /> : <Busy />;
  const groups = byRun(attention.data.waits);

  return (
    <section class="ff-needs-me">
      <h1>Needs me</h1>
      {attention.error && <ErrorBox error={attention.error} />}
      {groups.length === 0 && <p>Nothing needs you now.</p>}
      {groups.map(group => {
        const run = group[0].run;
        return (
          <section key={run.run_id} class="ff-needs-me-run" data-run={run.run_id}>
            <h2>
              <a href={links.run(run.run_id)}>{run.template}</a> <Status status={run.status} />
            </h2>
            <p class="ff-needs-me-run-id">
              {run.run_id}, started by {run.requester}
            </p>
            <Table head={["Waiting for", "Step", "Asks", "Progress", "Since"]}>
              {group.map(({ wait }) => (
                <tr key={wait.wait_id} data-wait={wait.wait_id}>
                  <td>
                    <a href={links.run(run.run_id)}>
                      {wait.kind}: {wait.decision_type}
                    </a>
                  </td>
                  <td>{wait.step_id}</td>
                  <td class="ff-needs-me-prompt">{wait.prompt}</td>
                  <td>{progress(wait).join("; ")}</td>
                  <td>
                    <Age at={wait.updated_at} />
                  </td>
                </tr>
              ))}
            </Table>
          </section>
        );
      })}
    </section>
  );
}
