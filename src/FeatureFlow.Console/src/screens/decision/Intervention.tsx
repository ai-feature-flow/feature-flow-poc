import type { Schema } from "../../api/client";
import { AnswerBar, clock } from "./AnswerBar";
import type { DecisionProps } from "./kinds";
import { Effects, recoveryAnswers } from "./recoveries";

/** Why the step stopped (A11): the engine's reason, what the step said, its attempts, and what sent it back each time. */
function Why({ wait, cause }: { wait: Schema<"WaitView">; cause: Schema<"WaitCauseView"> | null }) {
  if (cause === null)
    return (
      <div class="ff-dec-why">
        <p class="ff-dec-text">{wait.prompt}</p>
      </div>
    );
  const attempts =
    cause.attempts_used !== null && cause.attempts_allowed !== null ? `${cause.attempts_used} of ${cause.attempts_allowed} attempts used.` : null;
  return (
    <div class="ff-dec-why" aria-label="Why it stopped">
      <p>
        {cause.step_id}
        {cause.outcome ? ` ended ${cause.outcome}` : ""}: {cause.reason}
        {attempts && ` ${attempts}`}
      </p>
      {cause.said && <p class="ff-dec-said">It said: “{cause.said}”</p>}
      {cause.sent_back.length > 0 && (
        <table class="ff-dec-table" aria-label="Sent back">
          <thead>
            <tr>
              <th>Execution</th>
              <th>Sent back by</th>
              <th>Outcome</th>
              <th>Why</th>
            </tr>
          </thead>
          <tbody>
            {cause.sent_back.map(s => (
              <tr key={s.execution}>
                <td>{s.execution}</td>
                <td>{s.by_step ?? "–"}</td>
                <td>{s.outcome ?? "–"}</td>
                <td class="ff-dec-text">{s.text}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {cause.likely_to_repeat && (
        <p class="ff-dec-repeat">
          The last two times it was sent back for the same reason, so <b>Retry</b> and <b>Grant attempts</b> are likely to repeat this.
        </p>
      )}
    </div>
  );
}

/** An intervention (design-v2 s4.5): its recoveries at the top; why it stopped, earlier grants, what each answer does, executions. */
export function Intervention({ runId, wait, detail, plan, context }: DecisionProps) {
  const cause = wait.cause ?? null;
  const executions = detail.timeline.filter(t => t.step_id === wait.step_id);
  return (
    <AnswerBar runId={runId} wait={wait} context={context} answers={recoveryAnswers(wait, plan)}>
      <h3>Why it stopped</h3>
      <Why wait={wait} cause={cause} />
      {cause !== null && cause.grants.length > 0 && (
        <div class="ff-dec-callout" aria-label="Earlier grants">
          <p>
            Attempts were granted to {cause.step_id} {cause.grants.length} time{cause.grants.length === 1 ? "" : "s"} already:
          </p>
          <ul>
            {cause.grants.map(g => (
              <li key={g.at}>
                {clock(g.at)} {g.actor} granted {g.attempts}
                {g.reason ? `: ${g.reason}` : ""}
              </li>
            ))}
          </ul>
        </div>
      )}
      <h3>What each answer does</h3>
      <Effects wait={wait} />
      {executions.length > 0 && (
        <>
          <h3>Executions</h3>
          <table class="ff-dec-table" aria-label="Executions">
            <thead>
              <tr>
                <th>Time</th>
                <th>Event</th>
                <th>Detail</th>
              </tr>
            </thead>
            <tbody>
              {executions.map(t => (
                <tr key={t.sequence}>
                  <td>{clock(t.at)}</td>
                  <td>{t.kind}</td>
                  <td class="ff-dec-text">{t.detail ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </AnswerBar>
  );
}
