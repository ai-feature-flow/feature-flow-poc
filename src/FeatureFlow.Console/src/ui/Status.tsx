import "./Status.css";

// Known statuses get their own look. The class is chosen from this list, never built from the API's text.
const known: Record<string, string> = {
  starting: "running",
  running: "running",
  waiting: "waiting",
  completed: "done",
  failed: "failed",
  aborted: "failed",
  cancelled: "failed",
  stalled: "warning",
  stuck: "warning",
};

/** A run's or step's status as a badge. */
export function Status({ status }: { status: string }) {
  return <span class={`ff-status ff-status-${known[status] ?? "other"}`}>{status}</span>;
}
