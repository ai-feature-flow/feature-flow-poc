import type { Schema } from "../../api/client";
import { Age } from "../../ui/Age";
import { Table } from "../../ui/Table";

/** What the run has done, in order. */
export function Timeline({ entries }: { entries: readonly Schema<"TimelineEntry">[] }) {
  const ordered = [...entries].sort((x, y) => x.sequence - y.sequence);
  return (
    <section class="ff-run-section" aria-label="Timeline">
      <h2>Timeline</h2>
      {ordered.length === 0 ? (
        <p>Nothing yet.</p>
      ) : (
        <Table head={["When", "What", "Step", "Detail"]}>
          {ordered.map(e => (
            <tr key={e.sequence} data-sequence={e.sequence}>
              <td>
                <Age at={e.at} />
              </td>
              <td>{e.kind}</td>
              <td>{e.step_id}</td>
              <td class="ff-run-text">{e.detail ?? ""}</td>
            </tr>
          ))}
        </Table>
      )}
    </section>
  );
}
