# Claude Code proof: every agent role on `claude-code`, model `haiku`

Captured 2026-10-30 on macOS (Apple silicon, .NET 10 SDK, Claude Code 2.1.314),
against `ai-feature-flow/feature-flow-example`. Summary and design consequences
are in `docs/16` section 12. This file is the evidence.

## Exit criterion

> One short ticket runs through the real `ticket-delivery` plan with every agent
> role on `claude-code` model `haiku`. The change is authored by Claude Code, the
> PR is merged into an integration branch (not `main`), and the cost is known
> from the record store.

**Met by run `claude-proof-6`.** PR **#11** was merged into
`ff/epic/claude-proof-6`, merge commit `f9b74a269ee2cd7d8344dfc9d8d849397081d40c`,
by `ai-feature-flow-agent-access[bot]`. Fixture `main` is still `5dd0108`.

## Run `claude-proof-6`

Ticket `TICKET-CLAUDE-6`: add `WidgetStore.Contains(int id)` with a unit test.
Branch `ff/ticket/claude-proof-6` → `ff/epic/claude-proof-6`, both created from
fixture `main` at `5dd0108`. Temporal dev server on port 7333, own queue
`claude-proof-6`. Code at local commit `423adca`.

| # | step | outcome | evidence |
|---|---|---|---|
| 1 | plan_spec (planner) | completed | commit `15c99b1`, session `209d4382` |
| 2 | design (designer) | completed | commit `7acbf1b`, session `4bb2125d` |
| 3 | implement (implementer) | completed | commit `eb16872`: `Contains(int id)` only |
| 4 | run_tests | completed | unit locally, integration delegated to CI |
| 5 | review (reviewer) | findings_open | `f-review-1` `ticket-requirement-missing-test`, blocking |
| 6 | triage_findings | completed | selected `f-review-1` |
| 7 | fix_findings (fixer) | completed | commit `bd5e3e4`: the test, session `6df649d1` |
| 8 | verify_fix | completed | regression test present in the full diff |
| 9 | run_tests (loop) | completed | re-entered after verify_fix |
| 10 | review (loop) | completed | 0 findings |
| 11 | evaluate_readiness | ready | open=0 |
| 12 | open_pr | completed | PR #11 (first attempt `Infrastructure/server_error`, retried free) |
| 13 | wait_checks | completed | 3 checks success at `bd5e3e4` |
| 14 | gate_merge | approved | one decision, 09:48:27.40Z |
| 15 | merge_pr | completed | `AUTHORIZED integration branch 'ff/epic/claude-proof-6'` |

### The change was authored by Claude Code

`gh api repos/ai-feature-flow/feature-flow-example/commits/<sha>` shows each
commit on the remote. `gh api .../pulls/11/commits` lists exactly
`15c99b1 7acbf1b eb16872 bd5e3e4`.

Each commit is tied to the Claude Code session that made it through the CLI's
own transcript, `~/.claude/projects/<fixture-slug>/<session>.jsonl`. The commit's
SHA appears in that session's tool output (the `git commit` or `git log -1`
result), and every assistant turn reports `model: claude-haiku-5-5`:

| step | session | commit | model |
|---|---|---|---|
| plan_spec | `209d4382-cba5-4f34-82e9-181b54439a76` | `15c99b1` | claude-haiku-5-5 |
| design | `4bb2125d-4d0e-4055-867a-15187ab60815` | `7acbf1b` | claude-haiku-5-5 |
| implement | `20b25926-55ea-4da5-93cd-dd77864f0050` | `eb16872` | claude-haiku-5-5 |
| fix_findings | `6df649d1-dbf7-4938-a082-89e0728954dd` | `bd5e3e4` | claude-haiku-5-5 |

What the model wrote:

```diff
+    public bool Contains(int id)
+    {
+        lock (_gate)
+        {
+            return _widgets.Any(w => w.Id == id);
+        }
+    }
```

```diff
+    [Fact]
+    public void Contains_returns_true_after_add_and_false_for_unknown_id()
+    {
+        var store = new WidgetStore();
+        var widget = store.Add("alpha");
+
+        Assert.True(store.Contains(widget.Id));
+        Assert.False(store.Contains(404));
+    }
```

**No canned content.** Neither the method body nor the test name appears
anywhere in `spikes/02-real-run/src` or `plans` (grep: 0 matches).
`StepWork.ChangeFor` returns `null` for every step id, and the only reference
to `StepWork` is the review-14 A3 check that asserts this. The agent step fails
when HEAD does not move. Runs 3b and 5 show that check firing for real
(`plan_spec: the agent made no commit … reported as failed rather than faked`).

**What would make this come out differently?** A commit made by anything other
than the agent session would not appear in that session's transcript tool
output. A session on another model would report a different `model` on its
assistant turns and in the result's `modelUsage`. Canned content would have to
come from `StepWork`, which is empty, or appear in the source, which it does not.

### Hooks on macOS

Every agent session installs `agent-runtime-work/<run>.claude-settings.json`.
Each hook pipes its stdin payload through
`jq -c '{event:.hook_event_name,session:.session_id,tool:.tool_name}'` into
`records/<run>.hook-events.jsonl`. Run 6:

| session | SessionStart | PreToolUse | PostToolUse | Stop |
|---|---|---|---|---|
| `209d4382` plan_spec | 1 | 7 | 7 | 1 |
| `4bb2125d` design | 1 | 6 | 6 | 1 |
| `20b25926` implement | 1 | 3 | 3 | 1 |
| `6df649d1` fix_findings | 1 | 6 | 6 | 1 |

Tools seen at `PreToolUse`: Bash 11, Read 7, Edit 3, Write 1. **All four events
fired on macOS in every agent session.**

The reviewer is a `claude -p --tools ""` call with no settings loaded, so it
runs no tools and installs no hooks. Its evidence is the cost record (model and
cost) and the verdict in `records/claude-proof-6.findings.json`.

### Cost, from the record store

`records/claude-proof-6.cost.jsonl`. Input tokens include cache creation and
cache reads.

| step | runtime | model | in | out | USD | s |
|---|---|---|---|---|---|---|
| plan_spec | claude-code | claude-haiku-5-5 | 149642 | 2954 | 0.00570 | 22 |
| design | claude-code | claude-haiku-5-5 | 129946 | 1468 | 0.00365 | 16 |
| implement | claude-code | claude-haiku-5-5 | 62934 | 656 | 0.00236 | 11 |
| run_tests | github-actions | – | – | – | – | 50 |
| review | claude-code | claude-haiku-5-5 | 2804 | 529 | 0.00065 | 6 |
| fix_findings | claude-code | claude-haiku-5-5 | 133390 | 1353 | 0.00379 | 16 |
| run_tests | github-actions | – | – | – | – | 48 |
| review | claude-code | claude-haiku-5-5 | 6567 | 270 | 0.00067 | 6 |
| (orchestration: triage, verify, readiness, PR, checks, merge) | local / github | – | – | – | – | 17 |

**Run total: USD 0.0168**, 485,283 input and 7,230 output tokens, 200.7 s
compute across 15 cost records. The cost is the CLI's own `total_cost_usd`.
On a subscription that is a notional list-price figure, not money billed.

### The merge approval

There is one decision record: `gate_merge`, `ticket.merge_approval`, actor
`Human:stuart-parkinson`, at **09:48:27.40Z**. `wait_checks` was recorded at
09:48:25.92Z and the merge happened at 09:48:34Z. So the approval was recorded
while the workflow was waiting at the gate, not before.

The approval is made by the harness's `--human-approvers` stand-in on the
product owner's behalf, under the standing instruction to merge into an
integration branch. It is not a person clicking approve.

## The other runs, and what each one taught

| run | result | why it matters |
|---|---|---|
| `claude-proof-1` | PR **#9** merged into `ff/epic/claude-proof`, USD 0.0106 | Straight path: the reviewer (no ticket context) raised nothing, so the fixer never ran. **Flaw:** the harness approved every gate on every poll. `ticket.merge_approval` was recorded at 08:30:24.56Z, 2 s into the run and before any step had completed (`plan_spec` finished at 08:30:43), so the merge authorisation passed by construction. Its 67 KB decisions file is not committed; the first record's timestamp is quoted here. |
| `claude-proof-2` | dead end after `verify_fix`, reported `completed` | Reviewer (with ticket) raised "missing unit test", and the fixer authored it (`caedddb`). Then the workflow returned `completed` with no PR: **the Spike 1 interpreter never re-runs a step that already has an outcome**, so `verify_fix → run_tests` routed nowhere. |
| `claude-proof-3` | terminated | **Interference:** run 2's worker was still polling the shared `spike-2-real` queue and took run 3's `plan_spec`. It returned run 2's cached session. No commit and no push. Every later run used its own queue (`FF_QUEUE`). |
| `claude-proof-3b` | dead end after `verify_fix` | `plan_spec` made no commit: the planner stopped to ask a clarifying question ("Reply with 1, 2, or 3") that nobody can answer in `-p` mode. The harness approved `gate_spec` only once reached. The fixer fixed a non-blocking doc nit (`602baf5`), then the same dead end. |
| `claude-proof-4` | PR **#10** merged into `ff/epic/claude-proof-4`, USD 0.0114 | First run with the interpreter fix and the corrected harness. Straight path, reviewer raised nothing. One decision, after `wait_checks`. |
| `claude-proof-5` | terminated | The loop re-entered (`run_tests`, `review` ran twice) but `verify_fix` marked only the first of two findings fixed. The fixer had nothing left to change and escalated. The harness, re-approving each arrival, cycled `gate_escalate → replan` every 2 s. |
| `claude-proof-6` | **PR #11 merged**, USD 0.0168 | The exit criterion, above. |

### Findings from the runs

1. **The interpreter could not loop, and that looked like success.**
   `PlanReady.Ready` skips any step with an outcome, and `Route` did nothing
   for a target that had already run. So the plan's fix loop dead-ended, and
   with nothing ready the workflow returned `completed`, the same class as
   `docs/16` section 5 bug 2. Spike 2's `a3-authors` record sets each stop at
   `verify_fix` in the same way. Fixed (product owner approved) by clearing the
   target's outcome when a route re-enters it.
2. **Deny before execution works on macOS.** In run 5 the planner chained
   `git ls-files … && git log … && ls`. Claude Code checks each
   subcommand against the allowlist (`git add/commit/status/diff`), and with
   `--permission-prompts none` it denied the call before running it. The agent
   then stopped without editing. That is the per-tool control `docs/08` 2.5
   needs. In run 3b the planner instead asked a question and stopped. Both
   come from a prompt incoherence inherited from Spike 2: the planner is told
   `Write access: none` and to commit, and it is asked to create
   `docs/ticket-plan.md`, which already exists on `main` from Spike 2. The
   engine's no-commit check reported both as `failed` and routed to
   `gate_spec`, rather than faking progress.
3. **Reviewer variance (R4) is visible on haiku too.** With the same kind of
   diff the reviewer raised 0 findings (runs 1 and 4), a blocking
   missing-test finding (runs 2, 5, 6), or a non-blocking doc nit (run 3b).
4. `plan_spec` in run 6 logged `feature-flow.yaml unreadable … Work/not_found`
   right after the branch was created. This is GitHub read-after-create lag,
   and the next step read it fine.

## Setup used (this Mac only)

- `FF_SECRETS_FILE=/Users/stuart.parkinson/keys/feature-flow-secrets.json`
  (`model-default=haiku`). `CLAUDE_CODE_OAUTH_TOKEN` is in the shell, and
  `ANTHROPIC_API_KEY` is unset in every child.
- `DOTNET_ROLL_FORWARD=Major`: the fixture targets net9.0 and only the .NET 10
  runtime is installed. The fixture was not retargeted.
- `~/.temporalio/bin/temporal server start-dev --port 7333 --ui-port 8333 --db-filename <worktree>/.spike2/temporal.db --headless`
- Branches were created by hand from fixture `main` (`ff/epic/claude-proof[-N]`,
  `ff/ticket/claude-proof-N`). The engine creates neither.
- `FF_QUEUE=claude-proof-N dotnet run --no-build -- run --ticket TICKET-CLAUDE-N --branch ff/ticket/claude-proof-N --base ff/epic/claude-proof-N --run-id claude-proof-N --human-approvers stuart-parkinson`
  from `src/FeatureFlow.Spike.RealHarness`.
