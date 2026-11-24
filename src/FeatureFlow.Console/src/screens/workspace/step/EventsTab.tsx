import { useState } from "preact/hooks";
import type { Schema } from "../../../api/client";
import { Segmented } from "../../runs/parts";

type Event = Schema<"StepEventView">;
type Source = Schema<"StepEventSource">;

const sources: readonly (readonly [Source, string])[] = [
  ["timeline", "Timeline"],
  ["agent", "Agent"],
  ["override", "Overrides"],
  ["answer", "Answers"],
  ["finding", "Findings"],
];

const clock = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });

/**
 * A step's Events tab (design-v2 s4.4; mock-up 02): every event of the step merged from its sources (the run's timeline,
 * its agent's events, overrides on it, answers to its waits, its finding events), newest first, filtered by source. Text only.
 */
export function EventsTab({ events }: { events: readonly Event[] }) {
  const [source, setSource] = useState<Source | "all">("all");
  const count = (s: Source) => events.filter(e => e.source === s).length;
  const shown = events.filter(e => source === "all" || e.source === source).reverse();
  return (
    <>
      <p>
        <Segmented
          label="Source"
          value={source}
          options={[["all", `All (${events.length})`], ...sources.map(([s, label]) => [s, `${label} (${count(s)})`] as const)]}
          onChange={setSource}
        />
      </p>
      {shown.length === 0 ? (
        <p class="ff-muted">{events.length === 0 ? "The step has no events yet." : "No events from this source."}</p>
      ) : (
        <table aria-label="Events">
          <thead>
            <tr>
              <th>Time</th>
              <th>Exec</th>
              <th>Source</th>
              <th>Event</th>
              <th>Detail</th>
              <th>Who</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((e, i) => (
              <tr key={i} data-source={e.source}>
                <td class="ff-muted" title={e.at}>
                  {clock.format(new Date(e.at))}
                </td>
                <td>{e.execution ?? ""}</td>
                <td>{e.source}</td>
                <td class="ff-mono">{e.kind}</td>
                <td class="ff-text">
                  {e.finding_id && <span class="ff-mono">{e.finding_id} </span>}
                  {e.text}
                </td>
                <td>{e.actor ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
