import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../../api/client";
import { runKey, useLive } from "../../../api/live";
import { ArtifactLink, type ArtifactTarget } from "../../../app/viewer";
import { Age } from "../../../ui/Age";
import { ErrorBox } from "../../../ui/ErrorBox";
import { Tabs } from "../../../ui/Tabs";
import { useDecision } from "../../decision/DecisionPopup";
import { StepActions } from "../../steer/StepActions";
import { ActivityTab } from "./ActivityTab";
import { isActive, pathOf, runOf, shown, statusWaits, type StepNode } from "../tree/model";

type Entry = Schema<"TimelineEntry">;
type Artifact = Schema<"ArtifactView">;

/** What a timeline entry of the step ends an execution with. */
const ends: readonly string[] = ["completed", "auto_resolved", "attempt_failed", "retried", "exhausted", "escalated", "cancelled", "interrupted"];

interface Execution {
  number: number;
  start: string;
  end?: string;
  outcome?: string;
  /** What the run did next: the next step's name, and what it said when it ended there. */
  then?: string;
}

/** The step's executions from the run's timeline: one per start, each with how and when it ended and what followed. */
function executionsOf(timeline: readonly Entry[], stepId: string): Execution[] {
  const ordered = [...timeline].sort((a, b) => a.sequence - b.sequence);
  const list: Execution[] = [];
  ordered.forEach((e, i) => {
    if (e.step_id !== stepId) return;
    if (e.kind === "started") list.push({ number: list.length + 1, start: e.at });
    else if (ends.includes(e.kind) && list.length > 0 && list[list.length - 1].end === undefined) {
      const current = list[list.length - 1];
      current.end = e.at;
      current.outcome = e.kind === "completed" ? (e.detail ?? "completed") : e.detail ? `${e.kind}: ${e.detail}` : e.kind;
      const next = ordered.slice(i + 1).find(n => n.step_id !== "" && n.step_id !== stepId);
      if (next) current.then = shown(next.step_id);
    }
  });
  return list;
}

const time = (at: string) => new Date(at).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });

/** A step's status in words. */
function statusOf(status: string): string {
  if (status.startsWith("done(")) return `Done: ${status.slice(5, -1)}`;
  if (status.startsWith("waiting(")) return `Waiting: ${status.slice(8, -1).replaceAll("_", " ")}`;
  if (status === "exhausted") return "Attempts exhausted";
  return status.charAt(0).toUpperCase() + status.slice(1);
}

const forms: Record<string, string> = { agent: "Agent", action: "Action", decision: "Decision" };

/** The Needs you card's heading (mock-up 02): "Needs you: " and the wait's title, without the prefix when the title says it already. */
function headingOf(wait: Schema<"WaitView">): string {
  const title = wait.title ?? wait.prompt;
  const prefix = wait.can_answer ? "Needs you" : "Waits on someone else";
  return title.toLowerCase().includes(prefix.toLowerCase()) ? title : `${prefix}: ${title}`;
}

/**
 * A step selected in the tree (design-v2 s4.4; mock-up 02): its path, a red Needs you card when it waits on you (why first,
 * then Review and answer), a Running now card while it runs, four boxes at a glance, and tabs: Overview (its executions,
 * inputs, outputs, routes and prompt), Activity (its agent sessions' turns, live while it runs; open first for a running
 * agent step) and Artifacts; Findings and Events come in B2. Steer and Edit step are here, never on tree rows.
 */
export function StepDetail({ node }: { node: StepNode }) {
  const { step, wait, runId } = node;
  const run = runOf(node);
  const detail = useLive([runKey(runId)], () => read(api.GET("/runs/{id}", { params: { path: { id: runId } } })));
  const artifacts = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/artifacts", { params: { path: { id: runId } } })));
  const [tab, setTab] = useState<"overview" | "artifacts" | "activity" | "findings" | "events">(
    node.state === "running" && step.form === "agent" ? "activity" : "overview",
  );
  const decisions = useDecision();

  const executions = detail.data ? executionsOf(detail.data.timeline, step.id) : [];
  const mine = (artifacts.data?.artifacts ?? []).filter(a => a.step_id === step.id);
  const targetOf = (a: Artifact): ArtifactTarget => ({ runId, reference: a.ref, name: `${a.name} · exec ${a.execution}` });
  const lastRun = executions[executions.length - 1];
  const allowed = isActive(run.run) ? (detail.data?.overrides_allowed ?? []) : [];
  const decision = wait !== null && !statusWaits.includes(wait.kind);

  return (
    <div class="ff-stack" data-run={runId} data-step={step.id}>
      <div class="ff-crumbs">{pathOf(node)}</div>
      <div class="ff-runhdr">
        <h1>{node.label}</h1>
      </div>
      {detail.error && <ErrorBox error={detail.error} />}

      {wait && decision && (
        <section class="ff-box ff-needs" aria-label="Needs you">
          <div class="ff-needs-head">
            <h2>{headingOf(wait)}</h2>
            <span class="ff-muted ff-right-end">
              opened <Age at={wait.updated_at} />
            </span>
          </div>
          <div class="ff-pane-body">
            <div class="ff-why">
              <b>Why.</b> <Why wait={wait} />
            </div>
            {wait.can_answer && (
              <p>
                <button type="button" class="ff-btn ff-btn-primary" onClick={() => decisions.open(runId, wait)}>
                  {wait.kind === "question" ? "Answer" : "Review and answer"}
                </button>
              </p>
            )}
          </div>
        </section>
      )}
      {wait && !decision && (
        <section class="ff-box ff-needs" aria-label={wait.kind}>
          <div class="ff-needs-head">
            <h2>{wait.kind === "stalled" ? "Stalled" : "Stuck"}</h2>
            <span class="ff-muted ff-right-end">
              <Age at={wait.updated_at} />
            </span>
          </div>
          <div class="ff-pane-body ff-text" role="status">
            {wait.prompt}
          </div>
        </section>
      )}
      {node.state === "running" && (
        <section class="ff-box" aria-label="Running now">
          <header class="ff-pane-head">
            <h2>Running now</h2>
          </header>
          <div class="ff-pane-body">
            {forms[step.form] ?? step.form} {step.what}
            {lastRun ? (
              <>
                , execution {lastRun.number}, since <Age at={lastRun.start} />
              </>
            ) : step.changed_at ? (
              <>
                , since <Age at={step.changed_at} />
              </>
            ) : null}
            .
          </div>
        </section>
      )}

      <section class="ff-box ff-detail" aria-label={`Step ${step.id}`}>
        <header class="ff-pane-head">
          <h2>{node.label}</h2>
          <span class="ff-muted">
            <span class="ff-run-id">{runId}</span> · {step.id} · {step.form} · {step.what}
          </span>
          <span class="ff-right-end">{run.plan && <StepActions runId={runId} plan={run.plan} step={step} overridesAllowed={allowed} />}</span>
        </header>
        <div class="ff-glance">
          <div>
            <div class="ff-k">Status</div>
            <div class={`ff-v ff-state-${node.state}`}>{statusOf(step.status)}</div>
            {step.breakpoint && <div class="ff-sub">breakpoint set</div>}
            {step.skipped_with && <div class="ff-sub">skipped with {step.skipped_with}</div>}
          </div>
          <div>
            <div class="ff-k">Attempts</div>
            <div class={node.problem && step.attempts_allowed != null ? "ff-v ff-state-problem" : "ff-v"}>
              {step.attempts_allowed != null ? `${step.attempts_used ?? 0} / ${step.attempts_allowed}` : "–"}
            </div>
            <div class="ff-sub">
              {(step.attempts_granted ?? 0) > 0
                ? `${(step.attempts_allowed ?? 0) - (step.attempts_granted ?? 0)} + ${step.attempts_granted} granted`
                : `${step.executions} ${step.executions === 1 ? "execution" : "executions"}`}
            </div>
          </div>
          <div>
            <div class="ff-k">Last outcome</div>
            <div class="ff-v">{lastRun?.outcome ?? "–"}</div>
            {lastRun?.then && <div class="ff-sub">then {lastRun.then}</div>}
          </div>
          <div>
            <div class="ff-k">{forms[step.form] ?? "Form"}</div>
            <div class="ff-v">{step.what}</div>
            {step.protected && <div class="ff-sub">protected</div>}
          </div>
        </div>
        <Tabs
          label="Step"
          selected={tab}
          onSelect={setTab}
          tabs={[
            { id: "overview", label: "Overview" },
            { id: "activity", label: "Activity" },
            { id: "artifacts", label: "Artifacts" },
            { id: "findings", label: "Findings", disabled: "in B2" },
            { id: "events", label: "Events", disabled: "in B2" },
          ]}
        />
        <div class="ff-tabpanel" role="tabpanel">
          {artifacts.error && <ErrorBox error={artifacts.error} />}
          {tab === "overview" ? (
            <Overview step={step} wait={wait} executions={executions} artifacts={mine} targetOf={targetOf} runId={runId} />
          ) : tab === "activity" ? (
            <ActivityTab runId={runId} stepId={step.id} running={node.state === "running"} />
          ) : (
            <ArtifactsTab artifacts={mine} targetOf={targetOf} />
          )}
        </div>
      </section>
    </div>
  );
}

/** Why a step waits, from the wait: an intervention's cause (what it said, what sent it back, whether a retry is likely to repeat), else its summary. */
function Why({ wait }: { wait: Schema<"WaitView"> }) {
  const cause = wait.cause;
  if (!cause) return <span class="ff-text">{wait.summary ?? wait.prompt}</span>;
  const back = cause.sent_back.slice(-3);
  return (
    <span class="ff-text">
      {cause.reason}
      {cause.said && `. It last said: “${cause.said}”`}
      {back.length > 0 && `. Sent back ${cause.sent_back.length} ${cause.sent_back.length === 1 ? "time" : "times"}`}
      {back.map(b => `\n${b.by_step ? shown(b.by_step) : "sent back"}${b.outcome ? ` (${b.outcome})` : ""}: ${b.text}`).join("")}
      {cause.likely_to_repeat && "\nRetrying will likely repeat this unless something changes."}
    </span>
  );
}

function Overview({ step, wait, executions, artifacts, targetOf, runId }: {
  step: Schema<"PlanStepView">;
  wait: Schema<"WaitView"> | null;
  executions: readonly Execution[];
  artifacts: readonly Artifact[];
  targetOf: (a: Artifact) => ArtifactTarget;
  runId: string;
}) {
  const outputs = artifacts.filter(a => a.kind === "artifact" || a.kind === "evidence");
  const latestOutputs = outputs.filter(a => a.execution === Math.max(...outputs.map(o => o.execution)));
  const prompts = artifacts.filter(a => a.kind === "prompt");
  return (
    <>
      {executions.length === 0 ? (
        <p class="ff-muted">Not run yet.</p>
      ) : (
        <table aria-label="Executions">
          <thead>
            <tr>
              <th>#</th>
              <th>Time</th>
              <th>Outcome</th>
              <th>Then</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {executions.map(x => {
              const output = outputs.find(a => a.execution === x.number);
              return (
                <tr key={x.number}>
                  <td>{x.number}</td>
                  <td>
                    {time(x.start)}
                    {x.end && `–${time(x.end)}`}
                  </td>
                  <td>{x.outcome ?? "running"}</td>
                  <td>{x.then ?? ""}</td>
                  <td>
                    {output && <ArtifactLink target={targetOf(output)}>output</ArtifactLink>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <div class="ff-kv">
        <span class="ff-k">Inputs</span>
        <span>
          {wait && wait.consumed.length > 0
            ? wait.consumed.map((c, i) => (
                <span key={c.key}>
                  {i > 0 && " · "}
                  {c.ref ? <ArtifactLink target={{ runId, reference: c.ref, name: c.name }} /> : c.name}
                </span>
              ))
            : <span class="ff-muted">as the step's prompt names them</span>}
        </span>
        <span class="ff-k">Outputs</span>
        <span>
          {latestOutputs.length === 0 ? (
            <span class="ff-muted">none yet</span>
          ) : (
            latestOutputs.map((a, i) => (
              <span key={a.ref}>
                {i > 0 && " · "}
                <ArtifactLink target={targetOf(a)}>{a.name}</ArtifactLink>
              </span>
            ))
          )}
        </span>
        <span class="ff-k">Routes</span>
        <span>{step.outcomes.length === 0 ? "–" : step.outcomes.map(o => `${o.name} → ${o.route ?? "next"}`).join(" · ")}</span>
        <span class="ff-k">Prompt</span>
        <span>
          {prompts.length === 0 ? (
            <span class="ff-muted">none recorded</span>
          ) : (
            <ArtifactLink target={targetOf(prompts[prompts.length - 1])}>{prompts[prompts.length - 1].name}</ArtifactLink>
          )}
        </span>
      </div>
    </>
  );
}

function ArtifactsTab({ artifacts, targetOf }: { artifacts: readonly Artifact[]; targetOf: (a: Artifact) => ArtifactTarget }) {
  const ordered = [...artifacts].sort((a, b) => b.execution - a.execution);
  return ordered.length === 0 ? (
    <p class="ff-muted">The step has no artifacts yet.</p>
  ) : (
    <table aria-label="Artifacts">
      <thead>
        <tr>
          <th>Exec</th>
          <th>Artifact</th>
          <th>Kind</th>
          <th>Type</th>
        </tr>
      </thead>
      <tbody>
        {ordered.map(a => (
          <tr key={`${a.ref}\n${a.execution}\n${a.name}`}>
            <td>{a.execution}</td>
            <td>
              <ArtifactLink target={targetOf(a)}>{a.name}</ArtifactLink>
            </td>
            <td>{a.kind.replace("_", " ")}</td>
            <td>{a.type ?? ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
