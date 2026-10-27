# Review 11: brief 2, review 10 applied

Date: 2026-10-27. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/06`, `docs/09`-`docs/14` at commit 25ab768.

## Summary

**Good. Small changes, then brief 2 is complete.** A1-A11, B1-B12 and C1-C2 are applied. The doc 13 examples are clean and section 6 there shows the validation pass. Four semantic issues remain, all at the parent/branch boundary or in the schema, plus stale text that predates review 10 in docs 09, 10 and 14.

The flagged judgement calls are accepted (section D). Once this review is applied, **start Spike 1** (section E).

## A. Must fix

| # | Issue | Change |
|---|---|---|
| A1 | **Branches route to parent steps.** `docs/09` 7.0 says a branch may route non-terminal outcomes only to steps **inside** the branch. But in `docs/13` 2.1 and 2.2 the branches route `blocked: gate_epic_escalate`, `replan_parent: replan` and `blocked: delivery_gate`, all parent steps. What happens to the join and to sibling branches while that runs is undefined | A `run.child` branch reports only its terminal outcome and `replan_parent` to the parent; `blocked` is handled inside the child's own plan. A `replan_parent` is **handled by the interpreter**: the child's patch goes through `applyPatch` with the E2 rules (applied, pending human decision, rejected). It does **not** route to an agent `replan` step, which would re-plan from `steps.plan_tickets.outcome` and ignore the child's patch. Siblings keep running. State this in `docs/09` 7.0 and 13.3 and fix the examples |
| A2 | **`max_concurrency: 1` is not ordered delivery.** With `join: all`, if `deliver_api` fails, `deliver_web` still runs, because the join waits for every branch. Order "in the order added" is also an implicit property of a concurrency limit, not a declared dependency | Make order explicit. Recommended: `mode: expand` with a **chain** shape, generating steps linked by routes (`deliver_api` `completed` goes to `deliver_web`, and any other outcome goes to `delivery_gate`). Alternatively, a declared `order: sequential` with `stop_on: any_failed` on `parallel`. Pick one, say which, and fix 13 2.2 |
| A3 | **Two forms of the resume directive.** `docs/09` 6.2 and `docs/13` use `{ end: resume }`; `docs/09` 15.1.1 says `{ resume: true }`. Also, `end` is defined as a **run status**, and resume is not one | One form, outside `end`. Recommended: `{ resume: true }`. Remove `resume` from the `end` enum in `docs/10` |
| A4 | **The schema still allows privilege in step inputs.** `docs/10` section 4 `agent.run` inputs still accept `tools` and `capabilities`, and require `write_access`. The A1 rule from review 10 is in the prose only | Remove `tools` and `capabilities` from `agent.run` inputs. Make `write_access` optional (it defaults to the role's). The tightening check stays in the validator |

## B. Stale text

| # | Where | Change |
|---|---|---|
| B1 | `docs/09` 7.1 example | Still `completed: "end: completed"` and `replan_parent: apply_child_patch`, contradicting 7.0 directly above it |
| B2 | `docs/09` 10.1 and 14; `docs/10` 2 and 5-6; `docs/14` L6 | `artifactTypes`, `schemaVersion`, string directives (`"end: rejected"`), `apply_child_patch`, `roles/planner` without a package |
| B3 | Open-question sections in `docs/09` 18, `docs/10` 7, `docs/14` | Every question is answered (reviews 10 and 11). Replace them with "None open", or list only what is genuinely open |
| B4 | `docs/13` 2.1 and 2.2 | A deterministic expansion has `proposed_by: { type: agent }`. Add `engine` to `proposed_by.type` and use it. The expansion binds `repositories: "$.repositories"`, but the generated patch shows `params.repositories`. `environment: production` is an installation-specific name, the same problem as C3 in review 09; take it from configuration. `run.delivery_prs` is not in the `run` namespace (`docs/09` 8.3.1) |

Process: before pushing, grep for every superseded form named in reviews 09-11. Three of the four stale-text rows would have been caught that way.

## C. Small additions

| # | Issue | Change |
|---|---|---|
| C1 | The `outputs` cap (16 entries, 256 bytes) is a starting value | Spike 2 records actual output sizes per kind |
| C2 | `insert_after` with rewiring is argued, not measured | Spike 1 or 2: have the planner agent author patches with both forms on the fixture plans and record validation failure rates. This is cheap and settles it |

## D. Judgement calls (accepted)

Decided by the product owner, 2026-10-27.

| Call | Decision |
|---|---|
| `insert_after` rewires the target's routes | Accepted, subject to C2 |
| `outputs` cap of 16 entries and 256 bytes | Accepted as a starting value (C1) |
| Both epic fix paths shown; the choice is workflow content | Accepted. The engine prefers neither |
| `docs/07` configuration stays camelCase; plans and library content are snake_case | Accepted. The boundary is stated once, in `docs/09` section 1 |

## E. Next: Spike 1

Decided by the product owner, 2026-10-27. After applying this review, start **Spike 1, interpreter under failure** (`docs/08` section 2.1). It gates the other spikes.

| Item | Detail |
|---|---|
| Scope | As specified in `docs/08` 2.1, including the measurements and all nine exit criteria |
| Also in scope | The Cel.NET checks (`docs/09` 8.1: restricted checker, replay determinism, cost estimation, cost limit). C2 above if the agent runtime part is ready; otherwise Spike 2 |
| Code location | A `spikes/01-interpreter/` directory. Spike code, not production code: correctness of the measurements matters, polish does not |
| Infrastructure | Local Temporal dev server and fake providers first. Ask before adding anything that needs credentials or cloud resources |
| Output | `docs/15-spike-1-results.md`: measurements, exit criteria met or not, and which provisional decisions (brief Q6 threshold, CEL) close or change |
| Reviews | Push results as they become meaningful, not only at the end. The same review cycle applies |

