import type { ComponentChildren } from "preact";
import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { useAttention } from "../../app/attention";
import * as links from "../../app/links";
import { Age } from "../../ui/Age";
import { Badge } from "../../ui/Badge";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import { Marker } from "../../ui/Marker";
import { useDecision } from "../decision/DecisionPopup";
import { titleOf } from "../decision/kinds";
import { RunName, Segmented, stepPath, when } from "../runs/parts";
import { InlineQuestion } from "./InlineQuestion";
import { type Kind, kindOf, kinds, summaryOf } from "./kinds";
import "../runs/parts.css";
import "./NeedsMe.css";

type Item = Schema<"AttentionItem">;
type Recent = Schema<"RecentAnswerView">;

/** Parts joined with " › ". */
function Trail({ parts }: { parts: ComponentChildren[] }) {
  return (
    <>
      {parts.map((part, i) => (
        <span key={i}>
          {i > 0 && " › "}
          {part}
        </span>
      ))}
    </>
  );
}

/**
 * Where the wait is: each run from the tree's root down with its badge, the container of its parent's step that started it
 * (a generated branch id such as `epics.children_0` is shown as its container, `epics`), then the wait's step.
 */
function pathOf({ run, wait, path }: Item): ComponentChildren[] {
  const parts: ComponentChildren[] = [];
  [...(path ?? []), run].forEach((r, i) => {
    const container = r.parent_step_id?.split(".").filter(s => !/_\d+$/.test(s)).join(".");
    if (i > 0 && container) parts.push(stepPath(container));
    parts.push(<RunName run={r} />);
  });
  parts.push(stepPath(wait.step_id));
  return parts;
}

/** One open wait (mock-up 06): the triangle, kind and title, path, summary, age; a question answered here, the rest in their popup. */
function WaitItem({ item }: { item: Item }) {
  const decisions = useDecision();
  const { run, wait } = item;
  const kind = kindOf(wait);
  const title = titleOf(wait);
  const summary = summaryOf(wait);
  const tree = item.path?.[0]?.run_id ?? run.run_id;

  return (
    <article class="ff-item" aria-label={title} data-run={run.run_id} data-wait={wait.wait_id} data-kind={kind}>
      <Marker kind="waiting" />
      <div class="ff-item-main">
        <div>
          <span class="ff-kindtag">{kind}</span>
          <span class="ff-item-title">{title}</span>
        </div>
        <div class="ff-item-path">
          <Trail parts={pathOf(item)} />
        </div>
        {summary !== null && <div class="ff-item-why">{summary}</div>}
        {kind === "question" && <InlineQuestion runId={run.run_id} wait={wait} title={title} />}
      </div>
      <div class="ff-item-acts">
        <span class="ff-item-age">
          <Age at={wait.updated_at} />
        </span>
        {kind !== "question" && (
          <button type="button" class="ff-btn ff-btn-primary" onClick={() => decisions.open(run.run_id, wait)}>
            Review and answer
          </button>
        )}
        <a href={links.step(tree, run.run_id, wait.step_id)}>Go to step →</a>
      </div>
    </article>
  );
}

/** The caller's recent answers, folded (design-v2 s4.2). */
function Answered({ recent }: { recent: readonly Recent[] }) {
  return (
    <div class="ff-answered">
      <Fold summary={`Answered by you recently (${recent.length})`}>
        <table>
          <tbody>
            {recent.map(r => (
              <tr key={`${r.run.run_id}\n${r.wait_id}\n${r.at}`} data-wait={r.wait_id}>
                <td class="ff-muted">{when(r.at)}</td>
                <td>
                  <Trail parts={r.step_id ? [<RunName run={r.run} />, stepPath(r.step_id)] : [<RunName run={r.run} />]} />
                </td>
                <td>{r.title}</td>
                <td>
                  {r.choice}
                  {r.reason && `: “${r.reason}”`}
                  {r.result !== "recorded" && <span class="ff-muted"> ({r.result})</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Fold>
    </div>
  );
}

const filters = [["all", "All"], ...kinds] as const;

/**
 * Needs me (#/, #/needs; design-v2 s4.2, mock-up 06): every open wait the caller may answer across all run trees, oldest
 * first as GET /attention gives them, filtered by kind; and the caller's recent answers, folded.
 */
export function NeedsMe() {
  const attention = useAttention();
  const [filter, setFilter] = useState<Kind | "all">("all");
  if (attention.data === undefined) return attention.error ? <ErrorBox error={attention.error} /> : <Busy />;
  const { waits, recent } = attention.data;
  const shown = filter === "all" ? waits : waits.filter(i => kindOf(i.wait) === filter);

  return (
    <section class="ff-list-page ff-needs-me">
      <h1>
        Needs me <Badge tone="need">{waits.length}</Badge>
      </h1>
      <p class="ff-sub">Everything waiting on you, across all your run trees, oldest first. Each is also sent to you on Slack with a link straight here.</p>
      {attention.error && <ErrorBox error={attention.error} />}
      <div class="ff-toolbar">
        <Segmented label="Kind" value={filter} options={filters} onChange={setFilter} />
        <span class="ff-muted ff-toolbar-end">Oldest first</span>
      </div>
      {shown.length === 0 ? (
        <p>{waits.length === 0 ? "Nothing needs you now." : "Nothing of this kind needs you now."}</p>
      ) : (
        <div class="ff-box">
          {shown.map(item => (
            <WaitItem key={`${item.run.run_id}\n${item.wait.wait_id}`} item={item} />
          ))}
        </div>
      )}
      {recent && recent.length > 0 && <Answered recent={recent} />}
    </section>
  );
}
