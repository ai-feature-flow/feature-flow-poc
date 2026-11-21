import { api, read } from "../../api/client";
import { useLive } from "../../api/live";
import * as links from "../../app/links";
import { Age } from "../../ui/Age";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";

/** Every run the caller may read (GET /runs), in the API's order. */
export function RunList() {
  const runs = useLive(["runs"], () => read(api.GET("/runs")));
  if (runs.data === undefined) return runs.error ? <ErrorBox error={runs.error} /> : <Busy />;

  return (
    <section class="ff-runs">
      <h1>Runs</h1>
      {runs.error && <ErrorBox error={runs.error} />}
      {runs.data.runs.length === 0 ? (
        <p>No runs yet.</p>
      ) : (
        <Table head={["Run", "Template", "Status", "Started by", "Updated"]}>
          {runs.data.runs.map(run => (
            <tr key={run.run_id} data-run={run.run_id}>
              <td>
                <a href={links.run(run.run_id)}>{run.run_id}</a>
              </td>
              <td>{run.template}</td>
              <td>
                <Status status={run.status} />
              </td>
              <td>{run.requester}</td>
              <td>
                <Age at={run.updated_at} />
              </td>
            </tr>
          ))}
        </Table>
      )}
    </section>
  );
}
