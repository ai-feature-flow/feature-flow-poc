import { api, read, type Schema } from "../../api/client";
import { useLive } from "../../api/live";
import * as links from "../../app/links";
import { Age } from "../../ui/Age";
import { Status } from "../../ui/Status";
import { Table } from "../../ui/Table";

/**
 * The runs this run started. A child is marked when Needs me (GET /attention) holds one of its waits, so the mark covers
 * only the waits the caller may answer.
 */
export function Children({ children }: { children: readonly Schema<"RunSummary">[] }) {
  const attention = useLive(["attention"], () => read(api.GET("/attention")));
  const waiting = new Set(attention.data?.waits.map(i => i.run.run_id) ?? []);

  return (
    <section class="ff-run-section" aria-label="Child runs">
      <h2>Child runs</h2>
      <Table head={["Run", "Template", "Status", "Updated", ""]}>
        {children.map(child => (
          <tr key={child.run_id} data-run={child.run_id}>
            <td>
              <a href={links.run(child.run_id)}>{child.run_id}</a>
            </td>
            <td>{child.template}</td>
            <td>
              <Status status={child.status} />
            </td>
            <td>
              <Age at={child.updated_at} />
            </td>
            <td>{waiting.has(child.run_id) && <strong class="ff-run-mark">waits on you</strong>}</td>
          </tr>
        ))}
      </Table>
    </section>
  );
}
