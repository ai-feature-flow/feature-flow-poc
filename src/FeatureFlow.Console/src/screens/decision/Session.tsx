import { useId, useState } from "preact/hooks";
import { api, read } from "../../api/client";
import { invalidate, runKey, useLive } from "../../api/live";
import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import { type Answer, AnswerBar, clock, ReasonForm } from "./AnswerBar";
import type { DecisionProps } from "./kinds";

/** A full commit id, as the API takes one (SessionNames.IsCommit). */
const isCommit = (text: string) => /^[0-9a-f]{40}([0-9a-f]{24})?$/.test(text.trim());

function Commit({ label, verb, busy, onSend }: { label: string; verb: string; busy: boolean; onSend: (revision: string) => void }) {
  const [revision, setRevision] = useState("");
  const id = useId();
  return (
    <>
      <label for={id}>{label}</label>
      <input id={id} class="ff-mono" size={44} value={revision} placeholder="full commit id, pushed to the session's branch" onInput={e => setRevision(e.currentTarget.value)} />
      <button type="button" class="ff-btn ff-btn-primary" disabled={busy || !isCommit(revision)} onClick={() => onSend(revision.trim())}>
        {verb}
      </button>
    </>
  );
}

/** The session's checks so far, and a form to run one on a commit (POST /sessions/{sid}/checks). */
function Checks({ sid, steps }: { sid: string; steps: readonly string[] }) {
  const key = `session:${sid}`;
  const checks = useLive([key], () => read(api.GET("/sessions/{sid}/checks", { params: { path: { sid } } })));
  const [chosen, setStep] = useState("");
  const step = chosen || steps[0] || "";
  const [revision, setRevision] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Error>();
  const id = useId();
  const run = async () => {
    setBusy(true);
    setError(undefined);
    try {
      await read(api.POST("/sessions/{sid}/checks", { params: { path: { sid } }, body: { step, revision: revision.trim() } }));
      await invalidate([key]);
    } catch (e) {
      setError(e instanceof Error ? e : new Error(String(e)));
    } finally {
      setBusy(false);
    }
  };
  const list = checks.data?.checks ?? [];
  return (
    <>
      {list.length === 0 ? (
        <p class="ff-dec-muted">No checks run yet.</p>
      ) : (
        <table class="ff-dec-table" aria-label="Checks">
          <tbody>
            {list.map(c => (
              <tr key={c.check_id}>
                <td>{clock(c.at)}</td>
                <td>{c.step_id}</td>
                <td class="ff-mono">{c.revision.slice(0, 7)}</td>
                <td>{c.status}</td>
                <td class="ff-dec-text">{c.result ?? c.detail ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {steps.length > 0 && (
        <div class="ff-dec-param">
          <label for={id}>Check</label>
          <select id={id} value={step} onChange={e => setStep(e.currentTarget.value)}>
            {steps.map(s => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
          <input class="ff-mono" size={44} aria-label="Commit to check" placeholder="full commit id" value={revision} onInput={e => setRevision(e.currentTarget.value)} />
          <button type="button" class="ff-btn" disabled={busy || !isCommit(revision)} onClick={() => void run()}>
            Run checks
          </button>
        </div>
      )}
      {error && <ErrorBox error={error} />}
    </>
  );
}

/**
 * An interactive planning session (design-v2 s4.5; A15): Submit · Abandon…; how to open it locally (the `ff session`
 * command to copy), its branch, the drafts it was seeded with, and its checks with Run checks.
 */
export function Session({ runId, wait, context }: DecisionProps) {
  const sid = wait.session_id!;
  const detail = useLive([runKey(runId)], () => read(api.GET("/sessions/{sid}", { params: { path: { sid } } })));
  const [copied, setCopied] = useState(false);
  const session = detail.data?.session;
  const workspace = detail.data?.workspace;
  const command = session?.open_command ?? `ff session fetch ${sid} --out .ff-session`;

  const answers: Answer[] = [
    {
      label: "Submit…",
      tone: "primary",
      form: (_send, busy, act) => (
        <Commit
          label="Commit to submit"
          verb="Submit"
          busy={busy}
          onSend={revision => act(reason => read(api.POST("/sessions/{sid}/submit", { params: { path: { sid } }, body: { revision, reason } })))}
        />
      ),
    },
    {
      label: "Abandon…",
      tone: "danger",
      form: (_send, busy, act) => (
        <ReasonForm
          does={`Ends the session without a submission${workspace?.abandon_outcome ? `; ${wait.step_id} ends ${workspace.abandon_outcome}` : ""}.`}
          verb="Abandon"
          busy={busy}
          onSend={reason => act(() => read(api.POST("/sessions/{sid}/abandon", { params: { path: { sid } }, body: { reason } })))}
        />
      ),
    },
  ];

  return (
    <AnswerBar runId={runId} wait={wait} context={context} answers={answers}>
      {detail.error && <ErrorBox error={detail.error} />}
      <h3>Open it locally</h3>
      <div class="ff-dec-command">
        <code aria-label="Open command">{command}</code>{" "}
        <button
          type="button"
          class="ff-btn ff-btn-ghost"
          onClick={() =>
            void navigator.clipboard?.writeText(command).then(
              () => setCopied(true),
              () => setCopied(false),
            )
          }
        >
          {copied ? "Copied" : "Copy"}
        </button>
      </div>
      {session && (
        <dl class="ff-dec-facts">
          <dt>Workspace branch</dt>
          <dd class="ff-mono">
            {session.repository} · {session.branch}
          </dd>
          <dt>Based on</dt>
          <dd class="ff-mono">{session.base_revision.slice(0, 7)}</dd>
          <dt>Status</dt>
          <dd>{session.status}</dd>
          <dt>For</dt>
          <dd>{session.person}</dd>
        </dl>
      )}
      {workspace && <p class="ff-dec-text">{workspace.prompt}</p>}
      <h3>Drafts</h3>
      {detail.data && Object.keys(detail.data.drafts).length === 0 && <p class="ff-dec-muted">No drafts seeded.</p>}
      {Object.entries(detail.data?.drafts ?? {}).map(([path, text]) => (
        <Fold key={path} summary={path}>
          <pre class="ff-dec-yaml">{text}</pre>
        </Fold>
      ))}
      <h3>Checks</h3>
      <Checks sid={sid} steps={workspace?.checks_from ?? []} />
    </AnswerBar>
  );
}
