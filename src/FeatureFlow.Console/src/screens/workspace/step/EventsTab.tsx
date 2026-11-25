import { useState } from "preact/hooks";
import type { Schema } from "../../../api/client";
import { Segmented } from "../../runs/parts";
import "./step.css";

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
      <p class="ff-steptab-filter">
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
        <table aria-label="Events" class="ff-steptab">
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
                <td class="ff-muted ff-nowrap" title={e.at}>
                  {clock.format(new Date(e.at))}
                </td>
                <td>{e.execution ?? ""}</td>
                <td>{e.source}</td>
                <td class="ff-mono ff-nowrap">{e.kind}</td>
                <td class="ff-text ff-wrap">
                  {e.finding_id && <div class="ff-mono ff-nowrap">{e.finding_id}</div>}
                  {e.text}
                </td>
                <td class="ff-wrap">{e.actor ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
