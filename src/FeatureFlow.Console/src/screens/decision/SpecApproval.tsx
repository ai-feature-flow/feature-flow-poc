import { ErrorBox } from "../../ui/ErrorBox";
import { Fold } from "../../ui/Fold";
import { AnswerBar, clock, stepPath } from "./AnswerBar";
import { consumedOf, listOf, textOf, useDocument } from "./documents";
import { Consumed, gateAnswers, routeOf } from "./Gate";
import type { DecisionProps } from "./kinds";

/** A criterion's verification methods as a line: "unit tier, new or reused, semantically reviewed · “intent”". */
function verificationOf(criterion: Record<string, unknown>): string {
  const owner = criterion.owner as Record<string, unknown> | undefined;
  if (owner) return `Verified by its ${textOf(owner.kind)} ${textOf(owner.id)}`;
  const methods = listOf(criterion, "verification");
  if (methods.length === 0) return "No verification method";
  return methods
    .map(m => {
      const changes = Array.isArray(m.allowed_test_changes) ? (m.allowed_test_changes as unknown[]).map(textOf).join(" or ") : "new, reused or modified";
      const review = textOf(m.semantic_review) === "required" ? ", semantically reviewed" : "";
      const intent = textOf(m.intent);
      return `${textOf(m.tier)} tier test in ${textOf(m.repository)}, ${changes}${review}${intent ? ` · “${intent}”` : ""}`;
    })
    .join("; ");
}

/**
 * A spec approval (design-v2 s4.5, mock-up 04): Approve · Reject… (the reason; says where the plan goes and that the branch
 * resets) · a summary; the checks the plan passed; what changed (the documents' `changes`, LOG 508); the requirements with
 * the criteria covering each; the criteria with how each is verified; folded: the prompt's checklist and how the plan came
 * about; the raw documents a click away in the viewer.
 */
export function SpecApproval({ runId, wait, detail, plan, context }: DecisionProps) {
  const requirementsRef = consumedOf(wait, "requirements");
  const planRef = consumedOf(wait, "delivery_plan");
  const requirements = useDocument(runId, requirementsRef?.ref);
  const delivery = useDocument(runId, planRef?.ref);

  const reqs = listOf(requirements.doc?.data, "requirements");
  const criteria = listOf(delivery.doc?.data, "acceptance_criteria");
  const coveredBy = new Map<string, string[]>();
  for (const c of criteria)
    for (const s of listOf(c, "supports")) {
      const id = textOf(s.requirement);
      coveredBy.set(id, [...(coveredBy.get(id) ?? []), textOf(c.id)]);
    }
  const uncovered = reqs.filter(r => !coveredBy.has(textOf(r.id))).length;
  const changes = [...(requirementsRef?.changes ?? []), ...(planRef?.changes ?? [])];
  const checks = detail.timeline.filter(t => t.kind === "completed" && /(^|\.)(validate|check_[a-z_]+)$/.test(t.step_id));
  const history = detail.timeline.filter(t => t.step_id.split(".").includes("plan") && t.step_id !== wait.step_id);

  const route = routeOf(plan, wait.step_id, "rejected");
  const rejectDoes = `The plan goes back to ${route?.replace(" (clean_up)", "") ?? "the planner"} with your reason${route?.includes("(clean_up)") ? ", and the work branch is reset to where planning started" : ""}.`;
  const summary =
    reqs.length + criteria.length === 0
      ? null
      : `${reqs.length} requirement${reqs.length === 1 ? "" : "s"} · ${criteria.length} acceptance criteri${criteria.length === 1 ? "on" : "a"} · ${uncovered === 0 ? "all covered" : `${uncovered} not covered`}`;

  return (
    <AnswerBar
      runId={runId}
      wait={wait}
      context={context}
      answers={gateAnswers(wait, choice => (choice === "rejected" ? rejectDoes : "Implementation starts against these exact revisions."), {
        approved: "Approve",
        rejected: "Reject",
      })}
      aside={summary}
    >
      {checks.length > 0 && (
        <div class="ff-dec-okbox" aria-label="Checks">
          <p>
            <b>Checks:</b> {checks.map(c => `${c.step_id} ${c.detail ?? "completed"}`).join(" · ")}
          </p>
          <p class="ff-dec-muted">Approving ratifies these exact revisions: implementation starts against them, and they change only through a new approval.</p>
        </div>
      )}
      <h3>What changed</h3>
      {changes.length === 0 ? (
        <p class="ff-dec-muted">First version put to you: the documents list no changes.</p>
      ) : (
        <ul aria-label="What changed">
          {changes.map(c => (
            <li key={c}>{c}</li>
          ))}
        </ul>
      )}
      {requirements.error && <ErrorBox error={requirements.error} />}
      {delivery.error && <ErrorBox error={delivery.error} />}
      <h3>Requirements</h3>
      {reqs.length === 0 ? (
        <p class="ff-dec-muted">{requirementsRef ? "Reading the requirements…" : "This decision consumes no requirements."}</p>
      ) : (
        <table class="ff-dec-table ff-dec-ids" aria-label="Requirements">
          <thead>
            <tr>
              <th>Id</th>
              <th>Requirement</th>
              <th>Covered by</th>
            </tr>
          </thead>
          <tbody>
            {reqs.map(r => {
              const by = coveredBy.get(textOf(r.id));
              return (
                <tr key={textOf(r.id)}>
                  <td>{textOf(r.id)}</td>
                  <td>
                    <b>{textOf(r.title)}.</b> {textOf(r.behaviour)}
                  </td>
                  <td>{by ? <span class="ff-dec-ok">✓ {by.join(", ")}</span> : <span class="ff-dec-warn">not covered</span>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <h3>Acceptance criteria and how each is verified</h3>
      {criteria.length === 0 ? (
        <p class="ff-dec-muted">{planRef ? "Reading the delivery plan…" : "This decision consumes no delivery plan."}</p>
      ) : (
        <table class="ff-dec-table ff-dec-ids" aria-label="Acceptance criteria">
          <thead>
            <tr>
              <th>Id</th>
              <th>Criterion</th>
            </tr>
          </thead>
          <tbody>
            {criteria.map(c => (
              <tr key={textOf(c.id)}>
                <td>{textOf(c.id)}</td>
                <td>
                  <b>{textOf(c.title)}.</b> {textOf(c.behaviour)}
                  <div class="ff-dec-muted">{verificationOf(c)}</div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <Fold summary="What to check (from the approval prompt)">
        <p class="ff-dec-text">{wait.prompt}</p>
      </Fold>
      {history.length > 0 && (
        <Fold summary="How this plan came about">
          <ul>
            {history.map(t => (
              <li key={t.sequence}>
                {clock(t.at)} {stepPath(t.step_id)} {t.kind}
                {t.detail ? ` ${t.detail}` : ""}
              </li>
            ))}
          </ul>
        </Fold>
      )}
      <p class="ff-dec-muted">Raw documents, opened in the viewer:</p>
      <Consumed runId={runId} wait={wait} />
    </AnswerBar>
  );
}
