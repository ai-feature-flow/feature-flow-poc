import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import { type Answer, AnswerBar, clock, ReasonForm } from "./AnswerBar";
import type { DecisionProps } from "./kinds";

type Finding = Schema<"FindingView">;
type State = "open" | "declined" | "escalated" | "fixed";
interface Decision {
  state?: State;
  reason: string;
}

/** The name of the artifact a person's triage is sent as: std's fix_loop `person_triage` produces it on `triaged`. */
const artifactName = "person_triage";

const choices: { state: State; label: string }[] = [
  { state: "open", label: "Fix it" },
  { state: "declined", label: "Decline" },
  { state: "escalated", label: "Escalate" },
  { state: "fixed", label: "Mark fixed" },
];

/** A YAML scalar for text from the form: plain when it cannot be misread, else double-quoted (JSON's escapes are YAML's). */
const scalar = (text: string): string => (/^[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(text) ? text : JSON.stringify(text));

/** The person's triage (std:artifact_types/triage), built from the form's fields only; an undecided finding is a comment. */
export function triageYaml(findings: readonly Finding[], decisions: ReadonlyMap<string, Decision>): string {
  const lines = ["schema: 1", "entries:"];
  for (const f of findings) {
    const d = decisions.get(f.ledger_id);
    if (d?.state && d.reason.trim()) lines.push(`  - finding: ${scalar(f.ledger_id)}`, `    state: ${d.state}`, `    reason: ${JSON.stringify(d.reason.trim())}`);
    else lines.push(`  # ${f.ledger_id}: not decided yet`);
  }
  return lines.join("\n") + "\n";
}

const decided = (d: Decision | undefined) => d?.state !== undefined && d.reason.trim() !== "";

function Pills({ f }: { f: Finding }) {
  return (
    <>
      <span class="ff-mono ff-dec-fid">{f.ledger_id}</span>
      <span class={`ff-sev ff-sev-${f.severity}`}>{f.severity}</span>
      {f.blocking && <span class="ff-sev ff-sev-blocking">blocking</span>}
      <span class="ff-sev ff-sev-basis">{f.basis}</span>
      <span class="ff-dec-muted">{f.rule}</span>
    </>
  );
}

function Where({ f }: { f: Finding }) {
  return (
    <div>
      <div class="ff-dec-k">Where</div>
      <div class="ff-mono">{f.location.file}</div>
      {f.location.symbol && <div class="ff-dec-muted">{f.location.symbol}</div>}
      {f.location.criterion && <div class="ff-dec-muted">criterion {f.location.criterion}</div>}
    </div>
  );
}

function History({ runId, f }: { runId: string; f: Finding }) {
  const detail = useLive([runKey(runId)], () =>
    read(api.GET("/runs/{id}/findings/{finding}", { params: { path: { id: f.run_id ?? runId, finding: f.ledger_id } } })),
  );
  const history = detail.data?.history ?? [];
  const triager = [...history].reverse().find(e => e.kind !== "raised" && e.state === f.state);
  return (
    <>
      <div>
        <div class="ff-dec-k">Triager{triager ? ` (${clock(triager.at)})` : ""}</div>
        <b>{f.state}</b>
        {triager?.reason ? `: “${triager.reason}”` : ""}
      </div>
      <div>
        <div class="ff-dec-k">History</div>
        <ul class="ff-dec-history" aria-label={`History of ${f.ledger_id}`}>
          {history.map((e, i) => (
            <li key={i}>
              {clock(e.at)} {e.actor}: {e.kind} → {e.state}
              {e.reason ? ` (${e.reason})` : ""}
            </li>
          ))}
        </ul>
      </div>
    </>
  );
}

/** One finding that needs the person: what the reviewer said, the triager's view, its history, and the decision with its reason. */
function Card({ runId, f, decision, onChange }: { runId: string; f: Finding; decision: Decision | undefined; onChange: (d: Decision) => void }) {
  const d = decision ?? { reason: "" };
  return (
    <article class={`ff-fcard${decided(d) ? " ff-fcard-done" : ""}`} aria-label={`Finding ${f.ledger_id}`} data-finding={f.ledger_id}>
      <header class="ff-fhead">
        <Pills f={f} />
        <div class="ff-ftitle">{f.title}</div>
      </header>
      <div class="ff-fbody">
        <div class="ff-full">
          <div class="ff-dec-k">Reviewer said</div>
          <p class="ff-dec-text">{f.description}</p>
        </div>
        <div>
          <div class="ff-dec-k">Required correction</div>
          <p class="ff-dec-text">{f.required_correction ?? "None given."}</p>
        </div>
        <Where f={f} />
        {(f.proof || f.location.snippet) && (
          <div class="ff-full">
            <Fold summary="Proof and code">
              {f.proof && <p class="ff-dec-text">{f.proof}</p>}
              {f.location.snippet && <pre class="ff-dec-yaml">{f.location.snippet}</pre>}
            </Fold>
          </div>
        )}
        <History runId={runId} f={f} />
      </div>
      <div class="ff-fdecide">
        <div class="ff-seg" role="radiogroup" aria-label={`Decision on ${f.ledger_id}`}>
          {choices.map(c => {
            const off = c.state === "fixed" && !f.can_mark_fixed;
            return (
              <button
                key={c.state}
                type="button"
                role="radio"
                aria-checked={d.state === c.state}
                disabled={off}
                title={off ? (f.mark_fixed_refusal ?? undefined) : undefined}
                onClick={() => onChange({ ...d, state: c.state })}
              >
                {c.label}
              </button>
            );
          })}
        </div>
        {!f.can_mark_fixed && <span class="ff-dec-muted"> Mark fixed: unavailable, {f.mark_fixed_refusal ?? "nothing committed since it was raised"}.</span>}
        <textarea
          aria-label={`Reason for ${f.ledger_id}`}
          rows={2}
          placeholder="Your reason (required), recorded as you and posted on the pull request if you decline"
          value={d.reason}
          onInput={e => onChange({ ...d, reason: e.currentTarget.value })}
        />
      </div>
    </article>
  );
}

/** A finding read only: its id, title, state and who decided it. */
function ReadOnly({ f }: { f: Finding }) {
  return (
    <tr>
      <td class="ff-mono">{f.ledger_id}</td>
      <td>{f.severity}</td>
      <td>{f.title}</td>
      <td>
        {f.state}
        {f.decided_by_person ? ` (${f.decided_by_person})` : ""}
      </td>
    </tr>
  );
}

function Table({ label, findings }: { label: string; findings: readonly Finding[] }) {
  return (
    <table class="ff-dec-table" aria-label={label}>
      <thead>
        <tr>
          <th>Id</th>
          <th>Severity</th>
          <th>Title</th>
          <th>State</th>
        </tr>
      </thead>
      <tbody>
        {findings.map(f => (
          <ReadOnly key={f.ledger_id} f={f} />
        ))}
      </tbody>
    </table>
  );
}

/**
 * A person's triage of findings (design-v2 s4.5, mock-up 03; Q13, Q14): Send triage, disabled until every finding that
 * needs the person has a state and a reason; Escalate instead…; the tally and "next undecided ↓". One card per finding
 * needing the person (A6 `needs_person`); those a person already decided, unchanged, read only and folded; the rest of the
 * ledger folded. Sent as the step's `person_triage` artifact, YAML built from the form's fields only and shown under "what
 * will be sent"; a triage the API refuses (A10) shows its errors in place.
 */
export function Triage({ runId, wait, context }: DecisionProps) {
  const ledger = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/findings", { params: { path: { id: runId } } })));
  const [decisions, setDecisions] = useState<ReadonlyMap<string, Decision>>(new Map());
  const findings = ledger.data?.findings ?? [];
  const needing = findings.filter(f => f.needs_person);
  const byPerson = findings.filter(f => !f.needs_person && f.decided_by_person);
  const rest = findings.filter(f => !f.needs_person && !f.decided_by_person);
  const count = needing.filter(f => decided(decisions.get(f.ledger_id))).length;
  const yaml = triageYaml(needing, decisions);
  const send = wait.choices.find(c => c !== "escalate") ?? "triaged";
  const escalated = needing.filter(f => f.state === "escalated").length;
  const declinedBlocking = needing.length - escalated;

  const next = () => {
    const first = needing.find(f => !decided(decisions.get(f.ledger_id)));
    if (first) document.querySelector(`[data-finding="${CSS.escape(first.ledger_id)}"]`)?.scrollIntoView({ block: "start", behavior: "smooth" });
  };
  const answers: Answer[] = [
    {
      label: "Send triage",
      tone: "primary",
      disabled: ledger.data === undefined ? "Reading the findings" : count < needing.length ? "Decide every finding, with a reason, first" : null,
      body: { outcome: send, artifacts: { [artifactName]: yaml } },
    },
  ];
  if (wait.choices.includes("escalate"))
    answers.push({
      label: "Escalate instead…",
      form: (s, busy) => (
        <ReasonForm
          does="Leave the fix loop without triaging; the run goes to whoever handles its escalations."
          verb="Escalate"
          busy={busy}
          onSend={reason => s({ outcome: "escalate", reason })}
        />
      ),
    });

  return (
    <AnswerBar
      runId={runId}
      wait={wait}
      context={context}
      answers={answers}
      aside={
        <span class="ff-dec-tally" aria-label="Tally">
          <b>
            {count} of {needing.length}
          </b>{" "}
          decided
          {count < needing.length && (
            <>
              {" · "}
              <button type="button" class="ff-linkbtn" onClick={next}>
                next undecided ↓
              </button>
            </>
          )}
        </span>
      }
    >
      {ledger.error && <ErrorBox error={ledger.error} />}
      <div class="ff-dec-why">
        <b>Why you.</b> The triager {escalated > 0 ? `escalated ${escalated} finding${escalated === 1 ? "" : "s"}` : ""}
        {escalated > 0 && declinedBlocking > 0 ? " and " : ""}
        {declinedBlocking > 0 ? `declined ${declinedBlocking} blocking or demonstrated finding${declinedBlocking === 1 ? "" : "s"}` : ""}
        {needing.length === 0 ? "left nothing for you to decide" : ", which an agent may not clear on its own"}.
        {byPerson.length > 0 && ` ${byPerson.length} finding${byPerson.length === 1 ? "" : "s"} a person already decided stand as decided and are not asked again (shown below for context).`}
      </div>
      <h3>Needs your decision</h3>
      {needing.map(f => (
        <Card key={f.ledger_id} runId={runId} f={f} decision={decisions.get(f.ledger_id)} onChange={d => setDecisions(m => new Map(m).set(f.ledger_id, d))} />
      ))}
      {byPerson.length > 0 && (
        <Fold summary={`Decided by a person, not asked again (${byPerson.length}, read only)`}>
          <Table label="Decided by a person" findings={byPerson} />
        </Fold>
      )}
      {rest.length > 0 && (
        <Fold summary={`The rest of the ledger: ${rest.length} more (read only)`}>
          <Table label="The rest of the ledger" findings={rest} />
        </Fold>
      )}
      <Fold summary="How triage works">
        <p>
          A finding is a defect a reviewer agent reports. While any blocking finding is open, the run cannot leave verify: the fix loop's triager decides
          each open finding first. When it escalates one, or declines one that is blocking or demonstrated, the loop stops for you.
        </p>
        <table class="ff-dec-effects">
          <tbody>
            <tr>
              <th scope="row">Fix it (open)</th>
              <td>The fixer fixes it with a regression test; a reviewer verifies the fix; then tests and review run again.</td>
            </tr>
            <tr>
              <th scope="row">Decline</th>
              <td>Kept as declined while its code is unchanged; your reason is posted on the pull request when one is open.</td>
            </tr>
            <tr>
              <th scope="row">Escalate</th>
              <td>The loop leaves by its escalated exit, to whoever handles that.</td>
            </tr>
            <tr>
              <th scope="row">Mark fixed</th>
              <td>Only if something was committed since the finding was raised.</td>
            </tr>
          </tbody>
        </table>
      </Fold>
      <Fold summary={`Show what will be sent (${artifactName}, YAML)`}>
        <pre class="ff-dec-yaml" aria-label="What will be sent">
          {yaml}
        </pre>
      </Fold>
    </AnswerBar>
  );
}
