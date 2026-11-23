import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { Fold } from "../../ui/Fold";
import { AnswerBar, clock } from "./AnswerBar";
import { listOf, textOf, useDocument } from "./documents";
import { gateAnswers, routeOf } from "./Gate";
import type { DecisionProps } from "./kinds";

type Artifact = Schema<"ArtifactView">;

const isType = (a: Artifact, type: string) => a.type?.endsWith(`/${type}`) === true || a.type === type || a.name === type;

/** The latest artifact of `type` (the highest execution, then the latest written). */
const latest = (artifacts: readonly Artifact[], type: string): Artifact | undefined =>
  artifacts.filter(a => isType(a, type)).sort((a, b) => a.execution - b.execution || a.at.localeCompare(b.at)).at(-1);

/**
 * What proves the run's criteria: the latest trace report (std:fragments/trace) in the run or its nearest ancestor that
 * has one, and the run's own delivery plan for its criteria when none has (plan s2.3: tickets have no trace).
 */
function useEvidence(runId: string) {
  return useLive([runKey(runId)], async () => {
    let trace: { runId: string; ref: string } | null = null;
    let deliveryPlan: string | null = null;
    let id: string | null = runId;
    for (let depth = 0; id !== null && depth < 6 && trace === null; depth++) {
      const { artifacts } = await read(api.GET("/runs/{id}/artifacts", { params: { path: { id } } }));
      const found = latest(artifacts, "trace_report");
      if (found) trace = { runId: id, ref: found.ref };
      if (depth === 0) deliveryPlan = latest(artifacts, "delivery_plan")?.ref ?? null;
      if (trace === null) id = (await read(api.GET("/runs/{id}", { params: { path: { id } } }))).run.parent_run_id;
    }
    const { findings } = await read(api.GET("/runs/{id}/findings", { params: { path: { id: runId } } }));
    return { trace, deliveryPlan, open: findings.filter(f => f.state === "open" || f.state === "escalated").length };
  });
}

const passed = (c: Schema<"CheckView">) => c.conclusion === "success" || c.conclusion === "neutral" || c.conclusion === "skipped";

function Tick({ ok, children }: { ok: boolean | null; children: string }) {
  return <span class={ok === null ? "ff-dec-muted" : ok ? "ff-dec-ok" : "ff-dec-warn"}>{ok === null ? "–" : ok ? "✓" : "✗"} {children}</span>;
}

function PullRequest({ pr }: { pr: Schema<"PullRequestView"> }) {
  const state = [pr.state, pr.draft ? "draft" : "not draft", pr.mergeable === null ? null : pr.mergeable ? "mergeable" : "not mergeable"].filter(Boolean).join(", ");
  return (
    <>
      <div class="ff-dec-facts4">
        <div>
          <div class="ff-dec-k">Pull request</div>
          <a href={links.external(pr.url)} target="_blank" rel="noreferrer noopener">
            #{pr.number} ↗
          </a>{" "}
          {state}
        </div>
        <div>
          <div class="ff-dec-k">Branches</div>
          <span class="ff-mono">
            {pr.head} → {pr.base}
          </span>
        </div>
        <div>
          <div class="ff-dec-k">Head</div>
          <span class="ff-mono">{pr.head_revision.slice(0, 7)}</span>
        </div>
        <div>
          <div class="ff-dec-k">Size</div>
          {pr.additions === null ? (
            "–"
          ) : (
            <>
              <span class="ff-dec-ok">+{pr.additions}</span> <span class="ff-dec-bad">−{pr.deletions ?? 0}</span> in {pr.changed_files ?? pr.files.length} files
            </>
          )}
        </div>
      </div>
      <p class="ff-dec-muted">Title: {pr.title}</p>
      {pr.files.length > 0 && (
        <table class="ff-dec-table ff-dec-files" aria-label="Files">
          <thead>
            <tr>
              <th>File</th>
              <th>+</th>
              <th>−</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {pr.files.map(f => (
              <tr key={f.path}>
                <td class="ff-mono">{f.path}</td>
                <td class="ff-dec-ok">{f.additions}</td>
                <td class="ff-dec-bad">{f.deletions}</td>
                <td>{f.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

/**
 * A merge decision (design-v2 s4.5, mock-up 04): Approve merge · Reject… · Open pull request ↗ · Open review report (B2) ·
 * ticks; what Approve does when the engine may not merge (a `*.merge_action` gate: a person merges on the code host); the
 * pull request (A5) with its files and checks; the engine's own checks; each requirement's trace to tests, or the criteria
 * marked "no trace in this run".
 */
export function Merge({ runId, wait, detail, plan, context }: DecisionProps) {
  const pr = wait.pull_request ?? null;
  const evidence = useEvidence(runId);
  const trace = useDocument(evidence.data?.trace?.runId ?? runId, evidence.data?.trace?.ref);
  const delivery = useDocument(runId, evidence.data?.trace ? null : evidence.data?.deliveryPlan);
  const traced = listOf(trace.doc?.data, "requirements");
  const criteria = listOf(delivery.doc?.data, "acceptance_criteria");
  const engineChecks = detail.timeline.filter(t => t.kind === "completed" && t.step_id.startsWith("verify."));
  const merged = routeOf(plan, wait.step_id, "approved");

  const ci = pr === null || pr.checks.length === 0 ? null : pr.checks.every(passed);
  const upToDate = pr?.mergeable_state ? pr.mergeable_state !== "behind" && pr.mergeable_state !== "dirty" : null;
  const open = evidence.data?.open ?? null;
  const proved = evidence.data?.trace ? traced.length > 0 && traced.every(r => r.traced === true) : null;

  return (
    <AnswerBar
      runId={runId}
      wait={wait}
      context={context}
      answers={gateAnswers(
        wait,
        choice => (choice === "rejected" ? "The pull request is declined and the run ends as rejected." : "A person merges it on the code host."),
        { approved: "Approve merge", rejected: "Reject" },
      )}
      aside={
        <>
          {pr && (
            <a class="ff-btn ff-btn-ghost" href={links.external(pr.url)} target="_blank" rel="noreferrer noopener">
              Open pull request ↗
            </a>
          )}{" "}
          <button type="button" class="ff-btn ff-btn-ghost" disabled title="The review report comes in B2">
            Open review report ⧉
          </button>{" "}
          <span class="ff-dec-ticks" aria-label="Ticks">
            <Tick ok={ci}>CI passed</Tick> · <Tick ok={upToDate}>{`up to date with ${pr?.base ?? "its base"}`}</Tick> ·{" "}
            <Tick ok={open === null ? null : open === 0}>{`${open ?? "–"} open findings`}</Tick> · <Tick ok={proved}>every criterion proved</Tick>
          </span>
        </>
      }
      note={
        <p>
          <b>Approve</b> asks a person to merge it on the code host: the engine may not merge here. The run waits until it is merged
          {merged ? `, then goes on to ${merged}` : ""}.
        </p>
      }
    >
      <h3>Pull request</h3>
      {pr === null ? <p class="ff-dec-text">{wait.prompt}</p> : <PullRequest pr={pr} />}
      <div class="ff-dec-two">
        <div>
          <h3>CI{pr ? ` on ${pr.head_revision.slice(0, 7)}` : ""}</h3>
          {pr === null || pr.checks.length === 0 ? (
            <p class="ff-dec-muted">No checks read.</p>
          ) : (
            <table class="ff-dec-table" aria-label="Checks">
              <tbody>
                {pr.checks.map(c => (
                  <tr key={c.name}>
                    <td>
                      <Tick ok={c.conclusion === null ? null : passed(c)}>{c.name}</Tick>
                    </td>
                    <td>{c.conclusion ?? c.state}</td>
                    <td>
                      {c.url && (
                        <a href={links.external(c.url)} target="_blank" rel="noreferrer noopener">
                          log ↗
                        </a>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {pr && <p class="ff-dec-muted">Read {clock(pr.read_at)} by {pr.step_id}.</p>}
        </div>
        <div>
          <h3>Engine's own checks</h3>
          {engineChecks.length === 0 ? (
            <p class="ff-dec-muted">None recorded.</p>
          ) : (
            <table class="ff-dec-table" aria-label="Engine checks">
              <tbody>
                {engineChecks.map(t => (
                  <tr key={t.sequence}>
                    <td>{t.step_id}</td>
                    <td>{t.detail ?? "completed"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
      <h3>Each criterion and its proof</h3>
      {evidence.data?.trace ? (
        <table class="ff-dec-table" aria-label="Trace">
          <thead>
            <tr>
              <th>Requirement</th>
              <th>Tests</th>
              <th>Result</th>
            </tr>
          </thead>
          <tbody>
            {traced.map(r => (
              <tr key={textOf(r.id)}>
                <td class="ff-mono">{textOf(r.id)}</td>
                <td class="ff-mono">
                  {(Array.isArray(r.tests) ? r.tests : []).map(t => (
                    <div key={textOf(t)}>{textOf(t)}</div>
                  ))}
                </td>
                <td>{r.traced === true ? <span class="ff-dec-ok">proved</span> : <span class="ff-dec-warn">{(Array.isArray(r.gaps) ? r.gaps : []).map(textOf).join("; ") || "not proved"}</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <>
          <p class="ff-dec-muted">No trace in this run: only project and epic runs trace requirements to tests. The criteria and how each is verified:</p>
          {criteria.length > 0 && (
            <table class="ff-dec-table" aria-label="Criteria">
              <tbody>
                {criteria.map(c => (
                  <tr key={textOf(c.id)}>
                    <td class="ff-mono">{textOf(c.id)}</td>
                    <td>{textOf(c.title)}</td>
                    <td class="ff-dec-muted">no trace in this run</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
      <Fold summary="The decision as the engine put it">
        <p class="ff-dec-text">{wait.prompt}</p>
      </Fold>
    </AnswerBar>
  );
}
