# Review 14: Spike 2 first real run

Date: 2026-10-29. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/16-spike-2-results.md`, `spikes/02-real-run/` (results, plans, code) and `tools/gh_app_*.py` at commit 069f1a8.

## Summary

**Real progress on the plumbing; the exit criterion is not met, and one floor was broken.** The providers are verified one at a time against the real systems. Seven real bugs were found by running the code. The report is honest that the model did not write the code. No secrets are committed.

But four things change the verdict:

1. **Floor F1 was bypassed.** PR #3 was merged into **`main`** by `ai-feature-flow-agent-access[bot]`, after the harness approved every gate automatically. The harness comment says "the spike targets an integration branch"; it does not.
2. **The agent did not author the change.** `StepWork` applied canned content. The one provider the spike most needs to prove, the agent runtime doing real work, is the one that was not exercised. Review 13 said to **ask the product owner for each credential when needed**; a model credential was not asked for.
3. **Two checks are circular.** R4 ("same verdict twice") passes because `review.run` is a pure function of the diff hash, not a model. The `insert_after` measurement generated its patches in code, so it measured floor F3 refusals, not planner reliability.
4. **The plan and the organisation configuration do not match on decision types** (A5), so a run-start validator would have blocked this run.

**Spike 2 is not met.** "One ticket runs to a merged PR" means an agent wrote the change, without manual intervention other than expected decisions.

## A. Must fix

| # | Issue | Change |
|---|---|---|
| A1 | **F1 bypassed: default-branch merge without a human.** The harness auto-approves every gate, including `gate_merge` (`ticket.merge_approval`), and the PR base was `main`. The organisation configuration requires 2 human approvers with `agentCounts: false` for a default branch, and the engine did not enforce it. A floor must hold **whatever the harness or plan does** | (a) The engine enforces F1 itself: `scm.merge` into a default branch refuses unless a recorded **human** approval satisfying `policy.approval` exists, regardless of plan routing. A harness decision is not a human. (b) The spike targets an **integration branch**, as K6 and the harness comment intend; any default-branch merge goes through a real human approval from the product owner. (c) Add a test: the harness approves `gate_merge` for a `main` PR and the merge is **refused**. (d) Correct the false comment |
| A2 | **Code host protection for `main` is absent.** The bot could merge into `main` because nothing on GitHub stopped it | Add a ruleset on the fixture repository protecting `main`: required review by a human, no bypass for the app. Show that a push or merge to `main` with the app's or agent's token is refused, and that the ticket branch works. Defence in depth with A1 |
| A3 | **No real model authored the change.** Local Ollama models could not produce structured tool calls, so `StepWork` applied canned content. Review 13 decided: ask for each credential when needed | **Decided by the product owner, 2026-10-29:** use the **same OpenRouter credentials** the design agent itself runs on (Tencent Hy4 through OpenRouter), for both runtimes, for now. If you cannot obtain them yourself, **ask the product owner at the start**, before any run. Then re-run with the agent writing the change, and remove `StepWork` from the agent path. Record per runtime which model ran and whether structured tool use worked through the router. The Ollama finding is still useful: record that small local models fail at structured tool use, and which ones |
| A4 | **Credentials reach the agent through its environment**, where its tools can read them | (a) Model keys: per-run virtual keys with a spend cap and expiry from the router, or a local proxy that adds the key so the agent never holds it. Say which, and record the residual risk. (b) The agent's code host token is **`contents:write` (with the read GitHub requires) on one repository only**. Pull request, checks and merge permissions stay with the engine's provider calls. The broker's default grant includes `pull_requests:write`; give the agent a narrower grant |
| A5 | **Decision types do not match.** The plan's gates use `ticket.merge_approval`, `ticket.spec_approval`, `ticket.design_approval` and `ticket.escalation`. `organisation.yaml` keys `policy.approval` by `ticket`, `epic` and `defaultBranch`, and `chat.allowedDecisions` lists `ticket.gate` and `epic.gate`. `docs/09` 14 keys policy by decision type, so check 21 fails at run start. The validators checked the plan alone | Key `policy.approval` and `chat.allowedDecisions` by decision type. Validate the plan **against** the organisation configuration (checks 20-22) at run start, in the worker, not only in a throwaway script |
| A6 | `policy.replan.allowed_roles` (review 10 A1) is missing from the organisation configuration | Add it. Show one agent re-plan introducing a role outside the list becoming a pending human decision |
| A7 | **R4 is circular.** The verdict is "a pure function of the diff hash", so it is identical by construction. R4 exists because LLM review gates were unstable in the prototype | Re-run R4 with a real reviewer model after A3. Record the verdicts, and whether passing prior findings stabilised them |
| A8 | **The `insert_after` measurement is circular.** The patches were generated in code by cycling targets, so the 25% and 50% failure rates are floor F3 refusals on the protected `design` step, as the report half-acknowledges | Re-run with a planner model authoring patches from the readable form (after A3), as review 11 C2 asked. Until then, report it as not measured |

## B. Should fix

| # | Issue | Change |
|---|---|---|
| B1 | The `docs/16` 4 capability table is the right output. Claude Code's hooks fired on Windows, which is direct evidence for Spike W0. Copilot's `PreToolUse` result is rightly marked unproven | Update `docs/08` 2.4 with the W0 evidence. Re-test Copilot hooks with a first-party model after A3 |
| B2 | `docs/16` says "Answer: yes", and section 7 lists the `insert_after` rates as replacing the earlier estimate | Change the answer to **partly**: the plumbing is real, model-authored work is not shown, and F1 was bypassed. Remove the `insert_after` row from section 7 until A8 is done. Spike 3 does not start until Spike 2 is met |
| B3 | Four closed PRs remain on the fixture repository | Fine; note them. The product owner can delete them in the web UI if wanted |
| B4 | Carried from Spike 1: `docs/15` 6.3 says "roughly 10 activities wait" at 20 against 10, but its table shows 5 and 6; peak never above 9 of 10 and 25 activities for 20 runs are unexplained | Correct the sentence, explain both, run the third repetition, then write the pool-sizing note into `docs/07` |

## Process

Stopping to ask for a credential is not a failure of autonomy; it is the agreed working model. A run that substitutes canned content for the provider under test should stop and ask instead, and must never present the result as meeting the exit criterion.

## Answers to `docs/16` section 8

| # | Question | Answer |
|---|---|---|
| 1 | Tighten the App installation from organisation-wide to the fixture repository | **Yes.** Least privilege is the design (R6); install it on `feature-flow-example` only |
| 2 | A Copilot token or Anthropic key | Not for now: OpenRouter, as in A3 |
| 3 | Is `PreToolUse` absent on Copilot CLI? | Re-test with a first-party model after A3 (B1). Until then, plan for the `docs/08` 2.5 fallback on Copilot |
| 4 | Closed PRs #1-#4 | Leave them as the example's history (B3). PR #3's merge to `main` (14 lines in `docs/ticket-plan.md`) also stays: it is harmless, and it is the evidence for A1 |
| 5 | `max_context_tokens` for Copilot CLI | Not needed for version 1. The runtime's usage output is enough; record it as unknown |

