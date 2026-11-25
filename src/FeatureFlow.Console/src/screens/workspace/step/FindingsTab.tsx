import type { Schema } from "../../../api/client";
import * as links from "../../../app/links";
import { Age } from "../../../ui/Age";
import "./step.css";

type Detail = Schema<"StepDetail">;

/**
 * A step's Findings tab (design-v2 s4.4; mock-up 02): the findings the step raised or decided, or its finding steps recorded
 * (A3's `findings`, in ledger order), each with its state in the ledger and the last thing done to it here (the latest
 * finding event among the step's events), and a link to the run's whole ledger, the review report. Text only.
 */
export function FindingsTab({ detail, tree }: { detail: Detail; tree: string }) {
  const lastHere = new Map<string, Schema<"StepEventView">>();
  for (const e of detail.events) if (e.source === "finding" && e.finding_id) lastHere.set(e.finding_id, e);
  return (
    <>
      {detail.findings.length === 0 ? (
        <p class="ff-muted">The step has no findings.</p>
      ) : (
        <table aria-label="Findings" class="ff-steptab">
          <thead>
            <tr>
              <th>Id</th>
              <th>Severity</th>
              <th>Title</th>
              <th>Ledger</th>
              <th>Here</th>
            </tr>
          </thead>
          <tbody>
            {detail.findings.map(f => {
              const here = lastHere.get(f.ledger_id);
              return (
                <tr key={f.ledger_id} data-finding={f.ledger_id}>
                  <td class="ff-mono ff-nowrap">{f.ledger_id}</td>
                  <td>
                    {f.severity}
                    {f.blocking ? ", blocking" : ""}
                  </td>
                  <td class="ff-text">{f.title}</td>
                  <td>{f.state}</td>
                  <td class="ff-wrap">
                    {here ? (
                      <>
                        <span class="ff-text">
                          {here.kind}
                          {here.actor ? ` by ${here.actor}` : ""}
                        </span>{" "}
                        <span class="ff-muted">
                          <Age at={here.at} />
                        </span>
                      </>
                    ) : (
                      <span class="ff-muted">–</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <p class="ff-muted">
        <a href={links.review(tree, detail.run_id)} onClick={e => {
          e.preventDefault();
          links.openWindow(links.review(tree, detail.run_id), `review:${detail.run_id}`);
        }}>
          The run's full ledger ⧉
        </a>{" "}
        in its review report.
      </p>
    </>
  );
}
