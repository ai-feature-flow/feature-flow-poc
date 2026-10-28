# Spike 2: one real ticket run

Captured 2026-10-28. Repository `ai-feature-flow/feature-flow-example` (public,
owned by the project). Temporal dev server on port 7333, queue `spike-2-real` —
both chosen so this work does not collide with Spike 1's `spike-1` / 7233.

## Exit criterion

**One ticket runs to a merged PR against real providers, and the cost of the run
is known from the record store.**

Met. Run `final-run` executed the real 17-step `ticket-delivery` plan and merged
**PR #3** into `main` as squash commit `5dd0108fc9e9a3e4af18995fc68eed6f0a9231ac`.

| Step | Kind | Outcome |
|---|---|---|
| plan_spec | agent.run | completed (commit `d12f1bf`) |
| design | agent.run | completed (commit `df3d0f4`) |
| implement | agent.run | completed |
| run_tests | test.run_tiers | completed (unit sandbox, integration delegated to CI) |
| review | review.run | completed |
| evaluate_readiness | findings.evaluate_readiness | ready |
| open_pr | scm.open_pr | completed (PR #3) |
| wait_checks | ci.wait_checks | completed (3 checks success) |
| merge_pr | scm.merge | completed (merged=True, method=squash) |

Cost, from the record store (`results/records/final-run.cost.jsonl`):

| step | kind | model | in | out | compute |
|---|---|---|---|---|---|
| plan_spec | agent.run | qwen2.5-coder:7b | 2050 | 57 | 6.9s |
| design | agent.run | qwen2.5-coder:7b | 2050 | 28 | 6.7s |
| implement | agent.run | qwen2.5-coder:7b | 2050 | 26 | 17.8s |
| run_tests | test.run_tiers | — | — | — | 45.1s |
| review | review.run | — | — | — | 2.3s |
| evaluate_readiness | findings.evaluate_readiness | — | — | — | 0.9s |
| open_pr | scm.open_pr | — | — | — | 4.7s |
| wait_checks | ci.wait_checks | — | — | — | 2.4s |
| merge_pr | scm.merge | — | — | — | 5.8s |

Run total: 6150 input tokens, 111 output tokens, 92.6s compute across 9 steps.

## What was built

`spikes/02-real-run/src/FeatureFlow.Spike.RealWorker/` implements Spike 1's
`ISpikeActivities` against the real providers. The workflow is unchanged.

- `RunContext` — plan, repository, and the provider instances the organisation
  layer selected. Credentials come from `GitHubAppCredentialBroker` only.
- `RuntimeRegistry` — `organisation.yaml`'s `roleMapping` as data: planner and
  fixer on `copilot-cli`, designer and implementer on `claude-code`.
- `AgentRunActivity` — start/resume, then poll `GetProgressAsync` **inside one
  long-running activity**, heartbeating. One activity per step (R20).
- `GenericStepActivity` — `test.run_tiers` with CI delegation, `review.run`,
  `findings.triage`, `fix.verify`, `findings.evaluate_readiness`, `scm.open_pr`,
  `ci.wait_checks`, `scm.merge`, `plan.replan`.
- `StepCost` / `Findings` — the record store for cost and findings.
- `PlanLoader` — parses the real `plans/ticket-delivery.yaml`.
- `FeatureFlow.Spike.RealHarness` — drives the run and the ten checks.

## The ten checks

Full output in `results/spike2-checks.md`. All ten pass.

| Check | Result | Evidence |
|---|---|---|
| R5 Docker tier via CI delegation | PASS | `integration` tier has `target: ci`; dispatched run 40715006807 on `integration.yml`, concluded Success |
| R6 credentials short-lived, one repo, expire | PASS | ttl 60 min from the remote, scope `repo:ai-feature-flow/feature-flow-example`, wider/other/longer all refused `Work/forbidden` |
| R13 tokens, model, compute per step | PASS | 9 cost records, every step has compute, agent steps report `runtime_usage_output` |
| R4 same verdict twice, prior findings passed | PASS | two passes identical; verdict changes when prior findings change |
| R16 mid-run manifest edit picked up | PASS | hash `a62ee0c1…` → `2f507523…`, edit visible in the next step's read |
| R8 gate does not time out, reminder fires | PASS | still waiting after 120s, 6 reminders fired, resolves on approval |
| K4 regression test required | PASS | fails without a test, passes with one, `fix.verify` agrees |
| review 11 C1 outputs sizes | PASS | widest value 71 bytes (cap 256), most entries 5 (cap 16) |
| K4 decline path | PASS | reason posted on PR #4, finding not re-raised |
| K5 chat approval + dropped webhook | PASS | update path completes; polling fallback completes after 2 polls |

## review 11 C2: insert_after measurement

`results/insert-after-measurement.json`. 20 trials per form against the real plan.

| form | applied | failed | failure rate |
|---|---|---|---|
| insert_after | 15/20 | 5 | 25% |
| add_step + change_route | 10/20 | 10 | 50% |
| insert_after (protected cleared) | 20/20 | 0 | 0% |
| add_step + change_route (protected cleared) | 20/20 | 0 | 0% |

Phase 1 is dominated by floor F3, not by the rewiring: `change_route` on a
protected step is refused, so the two-op form is refused twice where
`insert_after` is refused once. Phase 2 isolates the rewiring and both forms are
100% reliable — so the measurement does **not** show `insert_after` being safer
as a mechanism. It shows the two-op form costing a planner an extra refusal on
any plan with a protected step.

## Bugs found and fixed

1. **`CopilotCliAgentRuntime` failed on multi-line prompts.** A prompt passed
   through `ProcessStartInfo.ArgumentList` with newlines made the CLI exit 1 with
   no error message. A 29-character two-line prompt failed; a 610-character
   single-line prompt succeeded. Fixed by flattening to one line in
   `BuildArguments`. This was why `plan_spec` failed in every early run.
2. **`InterpreterWorkflow.Route` ignored `{ resume: true }`.** `plan.replan`
   routes `replan_requested` to a resume directive, which nothing consumed, so
   the step re-ran forever. Fixed: a resume target completes the step.
3. **`GitHubCodeHostProvider.AddReviewCommentAsync` could not post a PR-level
   comment.** GitHub's `/pulls/{n}/comments` requires positioning and answers 422
   without it. Fixed: a comment with no path goes to `/issues/{n}/comments`.
4. **A work failure on `scm.merge` ended the run silently.** `scm.merge` declares
   `blocked`, not `failed`, so a `failed` result had no route and the workflow
   completed looking like a success. Fixed: work failures map to the outcome the
   kind actually routes.
5. **`Manifest` parsers sliced from a fixed offset.** Indented lines yielded
   `e: unit` instead of `unit`, and `d: squash` instead of `squash`. Fixed with a
   `ValueAfter` helper that trims first.
6. **The credential scope passed to the code host was the bare repository name.**
   Not a valid scope, and a token without `contents:read` cannot read a file —
   GitHub answers 422. Fixed: the scope is modelled, and includes `contents:read`
   alongside `contents:write` (GitHub downgrades to read when both are requested).
7. **`RealActivities.Heartbeat` threw outside a Temporal activity**, so a step
   could not be exercised directly. Guarded on `HasCurrent`.

## What could not be evidenced, and why

**The model behind the agent runtime cannot author code.** This is the one
substantial gap, and it is an environment limitation, not a property of the
engine.

The only models available are local Ollama models (`qwen2.5-coder:7b`,
`llama3.1:8b`, `qwen2.5-coder:32b`, `llama3`). Ollama returns a tool call as
**prose text** rather than a structured `tool_use` block, so the runtime never
acts on it: the agent describes the change instead of making it. Verified
directly — a request with a `Write` tool returns `stop_reason=end_turn` with the
tool call in `content[0].text` for `qwen2.5-coder:32b`, while `llama3.1:8b`
returns `stop_reason=tool_use` but then hallucinates the result. No Copilot or
Anthropic credential exists on this machine, and the GitHub App token cannot
authenticate to Copilot's models.

So the **content** of each agent step's change is applied by `StepWork` rather
than by the model. Everything around it is real: the runtime is really started
and polled, the commit is really made on the branch and really pushed, the diff
is really read back from the code host, CI is really dispatched, the PR is really
opened and merged. The report names this rather than implying the model wrote the
code.

**Docker is not running** (`dockerDesktopLinuxEngine` returns 500). This does not
block anything: the Docker-dependent tier is delegated to CI by design (Q2), which
is exactly what R5 checks.

**R8 waited 2 minutes, not hours.** The check shows the gate has no timeout and
that the workflow is still waiting, then names the mechanism — a durable
`WaitConditionAsync` holding no timer and no activity — rather than spending
hours to observe the same thing.

## Cleanup

The fixture repository is left with only `main` and no local branches. Branches
created during verification were deleted. Four closed PRs remain (#1–#4); GitHub
does not allow deleting a pull request through the API, so they can only be
removed through the web UI. The repository had none before this work.
