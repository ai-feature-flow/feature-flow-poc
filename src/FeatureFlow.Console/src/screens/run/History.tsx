import type { Schema } from "../../api/client";
import { Age } from "../../ui/Age";
import { Table } from "../../ui/Table";

interface Entry {
  key: string;
  at: string;
  actor: string;
  what: string;
  reason: string;
  result: string;
}

const ofAnswer = (a: Schema<"AnswerView">, i: number): Entry => ({
  key: `a${i}`,
  at: a.at,
  actor: a.actor,
  what: a.wait_id === "abort" ? "abort" : `${a.wait_id}: ${a.choice}`,
  reason: a.reason ?? "",
  result: a.result,
});

const ofOverride = (o: Schema<"OverrideView">): Entry => ({
  key: `o${o.override_id}`,
  at: o.at,
  actor: o.actor,
  what: [o.key, o.step_id ?? "", ...Object.entries(o.change).map(([k, v]) => `${k}: ${v}`)].filter(s => s !== "").join("\n"),
  reason: o.reason,
  result: o.result,
});

/** Every answer and override given on the run, oldest first. */
export function History({ answers, overrides }: { answers: readonly Schema<"AnswerView">[]; overrides: readonly Schema<"OverrideView">[] }) {
  const entries = [...answers.map(ofAnswer), ...overrides.map(ofOverride)].sort((x, y) => Date.parse(x.at) - Date.parse(y.at));
  return (
    <section class="ff-run-section" aria-label="History">
      <h2>History</h2>
      {entries.length === 0 ? (
        <p>No answers or overrides yet.</p>
      ) : (
        <Table head={["When", "Who", "What", "Reason", "Result"]}>
          {entries.map(e => (
            <tr key={e.key}>
              <td>
                <Age at={e.at} />
              </td>
              <td>{e.actor}</td>
              <td class="ff-run-text">{e.what}</td>
              <td class="ff-run-text">{e.reason}</td>
              <td>{e.result}</td>
            </tr>
          ))}
        </Table>
      )}
    </section>
  );
}
