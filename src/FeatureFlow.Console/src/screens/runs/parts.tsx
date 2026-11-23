import type { Schema } from "../../api/client";
import "./parts.css";

// Pieces Needs me and Runs share (mock-up 06): a run's name with its kind badge, a step's path, a choice of filters, and
// times as the pages show them. Every value is API text, rendered as text.

type Run = Schema<"RunSummary">;

/** A GitHub issue key (`owner/repo#12`) as the run lists show it (`#12`); any other key (`ACME-12`) as it is. */
const shortKey = (key: string): string => (/^[^\s#/]+\/[^\s#/]+#\d+$/.test(key) ? key.slice(key.indexOf("#")) : key);

/** What names a run: its tracker issue and title (`#198 GET /widgets/{id}`), else whichever of them it has, else its id. */
export function runLabel(run: Run): string {
  const key = run.tracker_issue ? shortKey(run.tracker_issue.key) : null;
  const title = run.title ?? run.tracker_issue?.title ?? null;
  return [key, title].filter(s => s !== null && s !== "").join(" ") || run.run_id;
}

const levels = new Set(["project", "epic", "ticket"]);

/** A run's kind badge (project, epic, ticket) and its name; no badge for a run recorded without a level. */
export function RunName({ run, strong = false }: { run: Run; strong?: boolean }) {
  const level = run.level ?? null;
  const label = runLabel(run);
  return (
    <span class="ff-runname" data-run-name={run.run_id}>
      {level !== null && <span class={`ff-kind ff-kind-${levels.has(level) ? level : "other"}`}>{level}</span>}
      {strong ? <b>{label}</b> : label}
    </span>
  );
}

/** A step's id as a path (`verify.fix.verify_fix` → `verify › fix › verify_fix`). */
export const stepPath = (stepId: string): string => stepId.split(".").join(" › ");

/** One of a few exclusive choices, as segmented buttons (`aria-pressed` on the chosen one). */
export function Segmented<T extends string>({ label, value, options, onChange }: {
  label: string;
  value: T;
  options: readonly (readonly [T, string])[];
  onChange: (value: T) => void;
}) {
  return (
    <span class="ff-seg" role="group" aria-label={label}>
      {options.map(([v, text]) => (
        <button key={v} type="button" aria-pressed={v === value} onClick={() => onChange(v)}>
          {text}
        </button>
      ))}
    </span>
  );
}

const clock = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit" });
const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short" });

/** A time as the lists show it: `today 08:40`, else `22 Nov 01:05`. */
export function when(at: string): string {
  const time = new Date(at);
  const today = new Date().toDateString() === time.toDateString();
  return `${today ? "today" : day.format(time)} ${clock.format(time)}`;
}

/** A duration between two times: `2 d 3 h`, `3 h 12 m`, `50 m`, `under 1 m`. */
export function took(from: string, to: string): string {
  const minutes = Math.floor((Date.parse(to) - Date.parse(from)) / 60_000);
  if (minutes < 1) return "under 1 m";
  const hours = Math.floor(minutes / 60);
  if (hours >= 24) return `${Math.floor(hours / 24)} d ${hours % 24} h`;
  return hours === 0 ? `${minutes} m` : `${hours} h ${minutes % 60} m`;
}
