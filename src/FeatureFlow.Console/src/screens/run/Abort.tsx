import { useState } from "preact/hooks";
import { api, read } from "../../api/client";
import { invalidate, runKey } from "../../api/live";
import { ErrorBox } from "../../ui/ErrorBox";
import { hasReason, Reason } from "../../ui/Reason";

/** Abort run (POST /runs/{id}/abort): a reason is required, and the run's pull requests may be closed with it. */
export function Abort({ runId }: { runId: string }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [closePullRequests, setClosePullRequests] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();
  const [result, setResult] = useState<string>();

  const submit = async (e: Event) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const reply = await read(api.POST("/runs/{id}/abort", { params: { path: { id: runId } }, body: { reason, close_pull_requests: closePullRequests } }));
      setResult(reply.result);
      setOpen(false);
      setReason("");
      invalidate([runKey(runId), "attention", "runs"]);
    } catch (failure) {
      setError(failure instanceof Error ? failure : new Error(String(failure)));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="ff-run-abort">
      {result !== undefined && <p role="status">Abort: {result}</p>}
      {!open ? (
        <button type="button" onClick={() => setOpen(true)}>
          Abort run
        </button>
      ) : (
        <form onSubmit={submit}>
          <Reason value={reason} onInput={setReason} />
          <label>
            <input type="checkbox" checked={closePullRequests} onChange={e => setClosePullRequests(e.currentTarget.checked)} /> Close its pull requests
          </label>
          <p>
            <button type="submit" disabled={busy || !hasReason(reason)}>
              Abort
            </button>{" "}
            <button type="button" onClick={() => setOpen(false)}>
              Cancel
            </button>
          </p>
          {error && <ErrorBox error={error} />}
        </form>
      )}
    </div>
  );
}
