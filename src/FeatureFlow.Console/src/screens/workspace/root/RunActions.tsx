import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../../api/client";
import { invalidate, runKey } from "../../../api/live";
import { usePopup } from "../../../app/popup";
import { Age } from "../../../ui/Age";
import { ErrorBox } from "../../../ui/ErrorBox";
import { Menu } from "../../../ui/Menu";
import { hasReason, labelOf, Reason } from "../../../ui/Reason";
import { Table } from "../../../ui/Table";
import { isActive } from "../tree/model";

/** Engine work for a later build (design-v2 s4.4, E4). */
const later = "needs engine work (B2)";

/**
 * A run's Run actions ▾ (design-v2 s4.4): abort, when the caller may and the run still runs; its history of answers and
 * overrides; restart from a step and re-run shown disabled, for they need engine work, and the policy in force, which is B2's.
 */
export function RunActions({ run, detail }: { run: Schema<"RunView">; detail: Schema<"RunDetail"> }) {
  const popups = usePopup();
  const mayAbort = isActive(run) && detail.overrides_allowed.includes("abort");
  return (
    <Menu
      label="Run actions"
      items={[
        {
          label: "Abort…",
          disabled: !mayAbort,
          note: mayAbort ? undefined : isActive(run) ? "you may not abort this run" : "the run has ended",
          onSelect: () => popups.open(<Abort runId={run.run_id} onDone={() => popups.close()} />, { label: `Abort ${run.run_id}` }),
        },
        {
          label: "History",
          onSelect: () => popups.open(<History answers={detail.answers} overrides={detail.overrides} />, { label: `History of ${run.run_id}`, wide: true }),
        },
        { label: "Restart from a step…", disabled: true, note: later, onSelect: () => undefined },
        { label: "Re-run", disabled: true, note: later, onSelect: () => undefined },
        { label: "Policy in force", disabled: true, note: "in B2", onSelect: () => undefined },
      ]}
    />
  );
}

/** Abort (POST /runs/{id}/abort): a comment is required, and the run's pull requests may be closed with it. */
function Abort({ runId, onDone }: { runId: string; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [closePullRequests, setClosePullRequests] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();

  const submit = async (e: Event) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      await read(api.POST("/runs/{id}/abort", { params: { path: { id: runId } }, body: { reason, close_pull_requests: closePullRequests } }));
      await invalidate([runKey(runId), "attention", "runs"]);
      onDone();
    } catch (failure) {
      setError(failure instanceof Error ? failure : new Error(String(failure)));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form class="ff-abort" onSubmit={submit}>
      <p>Aborting ends the run and every run under it. It cannot be undone.</p>
      <Reason value={reason} onInput={setReason} required />
      <label>
        <input type="checkbox" checked={closePullRequests} onChange={e => setClosePullRequests(e.currentTarget.checked)} /> Close its pull requests
      </label>
      <p>
        <button type="submit" class="ff-btn" disabled={busy || !hasReason(reason)}>
          {labelOf("Abort", true)}
        </button>
      </p>
      {error && <ErrorBox error={error} />}
    </form>
  );
}

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
  reason: o.reason ?? "",
  result: o.result,
});

/** Every answer and override given on the run, oldest first: who, what and why. */
function History({ answers, overrides }: { answers: readonly Schema<"AnswerView">[]; overrides: readonly Schema<"OverrideView">[] }) {
  const entries = [...answers.map(ofAnswer), ...overrides.map(ofOverride)].sort((x, y) => Date.parse(x.at) - Date.parse(y.at));
  return entries.length === 0 ? (
    <p>No answers or overrides yet.</p>
  ) : (
    <Table head={["When", "Who", "What", "Comment", "Result"]}>
      {entries.map(e => (
        <tr key={e.key}>
          <td>
            <Age at={e.at} />
          </td>
          <td>{e.actor}</td>
          <td class="ff-text">{e.what}</td>
          <td class="ff-text">{e.reason}</td>
          <td>{e.result}</td>
        </tr>
      ))}
    </Table>
  );
}
