import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { ArtifactLink } from "../../app/viewer";
import { Age } from "../../ui/Age";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import "./review.css";

type Report = Schema<"ReviewReport">;
type Pass = Schema<"ReviewPassView">;
type Finding = Schema<"ReviewFindingView">;

/** The ledger states, in the order the tally names them. */
const states = ["open", "escalated", "fixed", "declined"] as const;

const stateClass = (state: string): string =>
  state === "fixed" ? "ff-pill ff-pill-ok" : state === "open" || state === "escalated" ? "ff-pill ff-pill-wait" : "ff-pill ff-pill-neutral";

/**
 * A run's review report (A14; design-v2 s4.6), `#/runs/{tree}/review` for the run named and `#/runs/{tree}/review/{run}` for
 * a run in its tree, from `GET /runs/{id}/review`; read again on the run's changes. Made to be read in a window of its own
 * beside the pull request: the run and its pull requests; the current pass's angles, each with its verdict and what it
 * checked; the findings with their states and the latest reason; the external review's findings; the requirement trace;
 * and the earlier passes, folded. Every string from the API is text.
 */
export function ReviewPage({ tree, runId, own = false }: { tree: string; runId: string; own?: boolean }) {
  const report = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/review", { params: { path: { id: runId } } })));
  const data = report.data;
  if (!data) return <div class="ff-review">{report.error ? <ErrorBox error={report.error} /> : <Busy />}</div>;
  const passes = [...data.passes].reverse();

  return (
    <div class="ff-review ff-stack" data-run={runId}>
      <div class="ff-crumbs">
        <a href={links.treeRun(tree, runId, own)}>Workspace</a> › Review report
      </div>
      <div class="ff-runhdr">
        <h1>
          <span class="ff-run-title">Review: {data.title ?? data.run_id}</span>
          <span class="ff-right-end ff-review-prs">
            {data.pull_requests.map(pr => (
              <a key={`${pr.repository}#${pr.number}`} class="ff-btn ff-btn-ghost" href={links.external(pr.url)} target="_blank" rel="noopener noreferrer">
                {pr.repository}#{pr.number} ↗
              </a>
            ))}
          </span>
        </h1>
      </div>
      {report.error && <ErrorBox error={report.error} />}
      <Summary report={data} />
      {data.note && (
        <p class="ff-box ff-pane-body ff-text" role="note">
          {data.note}
        </p>
      )}
      {passes.length > 0 && <PassBox pass={passes[0]} runId={runId} current />}
      <FindingsBox label="Findings" findings={data.findings} passes={data.passes.length} empty="The review raised no findings." />
      <FindingsBox label="External review" findings={data.external} passes={0} empty="No external review findings." />
      <TraceBox trace={data.trace} runId={runId} />
      {passes.length > 1 && (
        <section class="ff-box" aria-label="Earlier passes">
          <header class="ff-pane-head">
            <h2>Earlier passes</h2>
          </header>
          <div class="ff-pane-body">
            {passes.slice(1).map(p => (
              <Fold key={p.pass} summary={`Pass ${p.pass} · ${timeOf(p)}`}>
                {() => <AngleTable pass={p} runId={runId} />}
              </Fold>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}

const clock = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });

const timeOf = (p: Pass): string => `${clock.format(new Date(p.started_at))}${p.ended_at ? `–${clock.format(new Date(p.ended_at)).split(", ").at(-1)}` : ", running"}`;

/** The report at a glance: passes, the findings by state, the external review and the trace. */
function Summary({ report }: { report: Report }) {
  const all = [...report.findings, ...report.external];
  const count = (state: string) => all.filter(f => f.finding.state === state).length;
  const trace = report.trace;
  return (
    <section class="ff-box ff-facts" aria-label="Summary">
      <div>
        <div class="ff-k">Passes</div>
        {report.passes.length === 0 ? <span class="ff-muted">none</span> : report.passes.length}
      </div>
      <div>
        <div class="ff-k">Findings</div>
        {all.length === 0 ? <span class="ff-muted">none</span> : states.map(s => `${count(s)} ${s}`).join(" · ")}
      </div>
      <div>
        <div class="ff-k">Trace</div>
        {trace === null ? (
          <span class="ff-muted">none in this run</span>
        ) : (
          `${trace.requirements.filter(r => r.traced).length} of ${trace.requirements.length} requirements traced`
        )}
      </div>
    </section>
  );
}

function PassBox({ pass, runId, current }: { pass: Pass; runId: string; current: boolean }) {
  return (
    <section class="ff-box" aria-label={current ? "Current review" : `Pass ${pass.pass}`}>
      <header class="ff-pane-head">
        <h2>
          {current ? "Current review" : "Review"}: pass {pass.pass}
        </h2>
        <span class="ff-muted">
          {pass.step_id} · {timeOf(pass)}
        </span>
      </header>
      <div class="ff-pane-body">
        <AngleTable pass={pass} runId={runId} />
      </div>
    </section>
  );
}

/** A pass's angles: each verdict and what was checked ("not recorded" for a report written before reviewers recorded them), its findings and report. */
function AngleTable({ pass, runId }: { pass: Pass; runId: string }) {
  return (
    <table class="ff-review-table" aria-label={`Angles of pass ${pass.pass}`}>
      <thead>
        <tr>
          <th>Angle</th>
          <th>Verdict</th>
          <th>What was checked</th>
          <th>Findings</th>
          <th>Report</th>
        </tr>
      </thead>
      <tbody>
        {pass.angles.map(a => (
          <tr key={a.step_id} data-angle={a.angle}>
            <td class="ff-review-angle">{a.angle}</td>
            <td class="ff-text">{a.execution === null ? <span class="ff-muted">no report in this pass</span> : a.verdict ?? <span class="ff-muted">not recorded</span>}</td>
            <td>
              {a.checked.length === 0 ? (
                a.execution === null ? null : <span class="ff-muted">not recorded</span>
              ) : (
                <ul class="ff-review-checked">
                  {a.checked.map((c, i) => (
                    <li key={i} class="ff-text">
                      {c}
                    </li>
                  ))}
                </ul>
              )}
            </td>
            <td>
              {a.execution === null ? "" : a.findings === 0 ? "none" : `${a.findings}${a.blocking > 0 ? ` (${a.blocking} blocking)` : ""}`}
            </td>
            <td>{a.report_ref && <ArtifactLink target={{ runId, reference: a.report_ref, name: `findings_report · ${a.angle} · exec ${a.execution}` }}>findings report</ArtifactLink>}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** Findings with their states and the latest reason: who gave it and when, and the pass and angle that raised each. */
function FindingsBox({ label, findings, passes, empty }: { label: string; findings: readonly Finding[]; passes: number; empty: string }) {
  return (
    <section class="ff-box" aria-label={label}>
      <header class="ff-pane-head">
        <h2>{label}</h2>
        <span class="ff-muted">{findings.length === 0 ? "" : `${findings.length} ${findings.length === 1 ? "finding" : "findings"}`}</span>
      </header>
      <div class="ff-pane-body">
        {findings.length === 0 ? (
          <p class="ff-muted">{empty}</p>
        ) : (
          <table class="ff-review-table" aria-label={`${label} table`}>
            <thead>
              <tr>
                <th>Finding</th>
                <th>Severity</th>
                <th>Raised</th>
                <th>State</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {findings.map(({ finding: f, raised_in_pass, reason, reason_by, decided_at }) => (
                <tr key={f.ledger_id} data-finding={f.ledger_id}>
                  <td>
                    <div class="ff-review-ftitle ff-text">{f.title}</div>
                    <div class="ff-mono ff-muted">
                      {f.ledger_id} · {f.location.file}
                      {f.location.symbol ? ` · ${f.location.symbol}` : ""}
                    </div>
                    <Fold summary="Description">
                      <p class="ff-text">{f.description}</p>
                      {f.required_correction && <p class="ff-text">Correction: {f.required_correction}</p>}
                      {f.proof && <p class="ff-text">Proof: {f.proof}</p>}
                    </Fold>
                  </td>
                  <td>
                    {f.severity}
                    {f.blocking ? ", blocking" : ""}
                    <div class="ff-muted">{f.basis}</div>
                  </td>
                  <td>
                    {[f.raised_by?.angle ?? f.raised_by?.source, passes > 1 && raised_in_pass !== null ? `pass ${raised_in_pass}` : null].filter(Boolean).join(" · ")}
                    {(f.reraises ?? 0) > 0 && <div class="ff-muted">raised again {f.reraises}×</div>}
                  </td>
                  <td>
                    <span class={stateClass(f.state)}>{f.state}</span>
                  </td>
                  <td>
                    {reason ? <div class="ff-text">{reason}</div> : <span class="ff-muted">–</span>}
                    {(reason_by || decided_at) && (
                      <div class="ff-muted">
                        {reason_by}
                        {decided_at && (
                          <>
                            {reason_by ? ", " : ""}
                            <Age at={decided_at} />
                          </>
                        )}
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}

/** Each requirement → its tests → whether it traced, and the gaps; the run's latest trace report. */
function TraceBox({ trace, runId }: { trace: Schema<"TraceView"> | null; runId: string }) {
  return (
    <section class="ff-box" aria-label="Trace">
      <header class="ff-pane-head">
        <h2>Each criterion and its proof</h2>
        {trace && <ArtifactLink target={{ runId, reference: trace.ref, name: "trace_report" }}>trace report</ArtifactLink>}
      </header>
      <div class="ff-pane-body">
        {trace === null ? (
          <p class="ff-muted">No trace in this run: it has no requirement trace step, or the step has not run yet.</p>
        ) : (
          <table class="ff-review-table" aria-label="Trace table">
            <thead>
              <tr>
                <th>Requirement</th>
                <th>Tests</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {trace.requirements.map(r => (
                <tr key={r.id}>
                  <td class="ff-mono">{r.id}</td>
                  <td class="ff-mono">
                    {r.tests.length === 0 ? <span class="ff-muted">none</span> : r.tests.map((t, i) => <div key={i}>{t}</div>)}
                  </td>
                  <td>{r.traced ? <span class="ff-review-ok">proved</span> : <span class="ff-review-gap">{r.gaps.join("; ") || "not proved"}</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
