# Engine mechanisms

Status: draft, 2026-10-31.

This document owns the contract of every step the engine provides: the agent
step, decisions, and the engine actions. Syntax is in `docs/03`; terms in
`docs/01`; who may do what in `docs/05`; the providers these steps call in
`docs/06`.

Every mechanism here passes the guardrail test in reverse: it is something a
workflow may use, and it carries no delivery policy of its own (P1, P2). Where
a mechanism has a choice to make, the choice is an input with a default, and
the standard library's templates set it.

---

## 1. Rules every step follows

| Rule | Detail |
|---|---|
| Contract | Each step's inputs, outcomes with classes, required capabilities, idempotency, limits, artifacts and audit events are stated here |
| Idempotency | Every step has a key: a hash of run id, step id, execution number and its stable inputs. A remote object the engine creates carries a hidden marker (a branch suffix, a pull request body comment, a commit trailer, a tracker label), so a retry finds and adopts it instead of creating a duplicate |
| Repository manifest | A step that touches a repository reads `feature-flow.yaml` from the branch when it starts and records the hash (RW11). The `library` section is read from the default branch only (`docs/07` section 4) |
| Errors | Provider errors are classified as infrastructure (retried with backoff, free) or work (routed by outcome) (`docs/06`). A step may reclassify because it knows the intent: an agent that ran out of time is a work result |
| Cannot answer | When a step cannot produce a trustworthy outcome (no manifest, a checkout not at the expected commit, results it cannot parse, a missing capability), it raises an **intervention** instead of guessing an outcome (P11, from FF) |
| Waiting | No step waits on human or external time inside a provider call. Human waits are durable workflow waits; external waits poll inside one long-running, heartbeating activity, or wake on an inbound event and then confirm by reading (`docs/02`) |
| Cost | Every step records tokens, model and compute time |
| Audit | Every step records its start, each attempt, each resume, and its outcome in the run timeline |

---

## 2. Catalogue

| Family | Step | Outcomes (class) |
|---|---|---|
| Agent | Agent step | Declared |
| Human | Decision | Declared |
| Composition | `run.child` | `completed` (success), `partial`, `rejected`, `failed` (failure) |
| Planning | `plan.expand` | `expanded` (success) |
| Source control | `scm.catch_up` | `updated` (success), `conflict` (blocked) |
| | `scm.pull_request` | `ready` (success) |
| | `scm.merge` | `merged` (success), `base_moved` (blocked), `checks_failed` (failure), `declined` (failure) |
| | `scm.revert` | `opened` (success) |
| Waiting | `ci.checks` | `passed` (success), `failed` (failure) |
| | `wait.condition` | `met` (success), `unmet` (failure) |
| Testing and analysis | `test.run` | `passed` (success), `failed` (failure), `baseline_failed` (blocked) |
| | `test.integration` | `passed` (success), `failed` (failure) |
| | `analysis.run` | `passed` (success), `failed` (failure) |
| Evidence | `artifact.validate` | `valid` (success), `invalid` (failure) |
| Findings | `findings.record` | `recorded` (success), `unavailable` (blocked) |
| | `findings.triage` | `applied` (success) |
| Repository | `repo.inspect` | `inspected` (success) |
| Artifacts | `artifact.compose` | `composed` (success) |
| | `artifact.import` | `imported` (success) |
| | `artifact.publish` | `published` (success), `conflict` (blocked) |
| Tracker and notifications | `tracker.update` | `done` (success), `not_found` (failure) |
| | `notify.send` | `sent` (success) |
| Delivery | `delivery.report` | `complete` (success), `partial` (failure) |

Twenty-one actions, down from forty step kinds in the first design. What went:

| First design | Now |
|---|---|
| `review.run`, `fix.verify`, `findings.evaluate_readiness` | Agent steps with reviewer and fixer roles, `findings.record` and `findings.triage`, and route guards on the `findings` namespace. Assembled by library fragments (RW5) |
| `human.gate`, `human.session`, `policy.auto_approve`, `delivery.gate` | The decision (section 4) |
| `plan.replan`, `plan.validate` | Planning authority on an agent step (section 6.1); validation is part of applying a patch |
| `scm.create_branch` | The engine creates a run's work branch the first time a step touches the repository |
| `scm.open_pr`, `scm.update_pr` | `scm.pull_request` |
| `scm.merge_base_forward` | `scm.catch_up` |
| `ci.trigger`, `ci.read_results` | `test.run` with a tier delegated to CI |
| `wait.for_state` | `wait.condition` |
| `test.run_tiers`, `test.run_composition` | `test.run`, `test.integration` |
| `delivery.deploy_order`, `delivery.deploy`, `delivery.gate` | A `for_each` chain of `scm.merge` steps (`docs/03` section 8.2), a decision, and `delivery.report` (RW15) |
| `tracker.create`, `.update`, `.transition`, `.link`, `.comment` | `tracker.update`, plus declarative `tracker` on any step |
| `artifact.bundle` | `artifact.compose` |

---

## 3. The agent step

Runs an agent with a role and a task, in a sandbox, and verifies what it
reports.

| Element | Detail |
|---|---|
| Form | Activity on an agent worker, routed to a task queue by the role's required capabilities |
| Inputs | `agent` settings and `with` bindings (`docs/03` section 2.2) |
| Requires | Sandbox, agent runtime and VCS providers. Hooks and session resume where the role requires them |
| Outcomes | Declared |
| Idempotency | Re-attach to the sandbox by key; resume the agent session by its recorded reference |
| Limits | Engine defaults: attempts 3, timeout 4 hours, correction turns 2 |
| Artifacts | In: the rendered task, skills, resources, consumed artifacts, the run context. Out: declared artifacts, the transcript, the record of files opened, the session reference |

### 3.1 Lifecycle of an attempt

1. **Prepare.** Create or re-attach the sandbox from the repository manifest;
   check out the work branch (read-only if the role has no write access); run
   the manifest's `setup` commands; stage consumed artifacts, skills and
   library resources read-only, with a manifest listing them. The manifest also
   gives the run's baseline commit and parent branch for the repository, and the
   agent's output folder. A step that consumes the reserved name
   `findings_ledger` is given the ledger's current view, every finding with its
   ledger id and state, in the shape of a findings report (LOG 63).
2. **Start.** Render the task, prepend the library's reporting preamble, and
   start the agent runtime in the sandbox with the role's tools and toolsets.
3. **Supervise.** Heartbeat. About every 30 seconds, ask the VCS provider
   whether the working copy has new commits; if so, push them to the hidden
   work-in-progress reference. Serve hook calls, the engine tools (section 3.3)
   and questions.
4. **Collect.** When the agent reports, read its result from the runtime (the
   report is a hint; the read confirms, `docs/02`). Commit leftover changes
   unless `on_uncommitted: fail`.
5. **Verify.** Check the reported outcome is declared; check produced artifacts
   and outputs against their contracts; for a role with no write access, check
   nothing was committed. Return errors to the same session for up to
   `correction_turns` turns. Still invalid: the attempt fails.
6. **After changes.** If the step committed, run the manifest's `after_changes`
   commands (formatters, generators) in the same sandbox and commit what they
   change as part of the step (from FF).
7. **Finish.** Move the work branch to the final commit, once. Record the
   summary for the run context. Store the transcript.

### 3.2 Resume after an interruption

| What survived | What happens |
|---|---|
| The sandbox | The new worker re-attaches by key. Uncommitted files are kept. The runtime reloads the conversation from the recorded session reference. Nothing comes from the old worker's memory (Spike 3 found that dependency, `archive/docs/18` section 4) |
| Nothing | A new sandbox, checked out at the hidden reference's last commit. A fresh session, told that the previous attempt was interrupted |

A resume is free if the agent made progress since the last one (`docs/01`
section 4). Commands in the sandbox are started and polled, never awaited, so a
long test run survives a worker restart.

### 3.3 The agent reporting contract

The engine serves a small set of tools to the agent through its MCP server
(Model Context Protocol, the standard way agent runtimes call external tools),
authenticated with a per-step token. The library's reporting preamble tells the
agent how to use them.

| Tool | Offered when | Effect |
|---|---|---|
| `report` | Always | The agent's result: one declared outcome, a short summary (at most 2 KB, used for the run context), outputs, and the paths of the artifacts it produced. May be called again to correct; the last call counts |
| `ask_human` | `limits.questions` is set | A question, with up to 4 suggested answers and optional context. Blocks until answered or timed out (section 3.4) |
| `checkpoint` | The role has write access | Commit now and have the engine push. A convenience; commit detection is the backstop |
| `inspect_plan` | `planning` is set | The current plan in its readable form |
| `propose_patch` | `planning` is set | A patch to this plan or the parent's, validated at once; errors come back to the agent |
| `run_check` | Interactive sessions only | Run a named later step's checks now (section 4.4) |

A runtime that cannot call MCP tools reports by writing the same `report` data
to a file in its output folder. An agent that ends without reporting has failed
its attempt.

A `report` with a `success` outcome must give every output the step declares,
each of its declared type (a `number` is whole). A missing or mistyped output
is an output contract error: the errors go back to the agent for correction
turns, like an invalid patch (LOG 76, 79). The interpreter's core refuses such
a report from M2; correction turns arrive with the agent step in M4.

### 3.4 Questions (`ask_human`)

From FF (RW14). The agent asks; the run opens a **question** decision
(`docs/01` section 7.1) and notifies; the agent's session waits, with its idle
and duration limits paused.

| Rule | Detail |
|---|---|
| Limit | `questions.max` per attempt (default 3). Past it, the tool tells the agent to use its judgement |
| Timeout | `questions.timeout` (default 4 hours). On timeout the agent is told that no answer came, and carries on |
| Who answers | A human allowed by policy for the step's question decision type. Auto-resolution never answers a question |
| Durability | The open question lives in workflow state and the record store, not in worker memory. If the worker restarts, the sandbox and the waiting agent survive; the new worker re-attaches and serves the answer when it arrives. Spiked in SP2 (`docs/spikes/sp2-results.md`), except the re-attach, which the spike's harness did not exercise |

### 3.5 Run context

Each agent step's summary is appended to the run's **context**, an artifact
given to later agent steps with `context: full` (from FF's `context.md`). A
child run's context starts with its parent's. A reviewer with `context: none`
sees only its task and consumed artifacts.

### 3.6 Outputs the engine publishes for every agent step

Besides the outputs the agent reports, the engine publishes these, so a route
can act on them (the engine itself never fails a step on them):

| Output | Meaning |
|---|---|
| `coverage_known` | Whether the record of files the agent opened is verified (from hooks or streamed tool calls) |
| `changed_files_unread` | How many files changed on the work branch since its parent the agent did not open |
| `commits` | How many commits the step added |

The standard review fragment routes a reviewer with `changed_files_unread > 0`
back for another attempt (`docs/08`); a specialised review need not.

### 3.7 Write access and pushing

| Rule | Detail |
|---|---|
| Enforced twice | By the sandbox (a read-only checkout for `none`) and, where the runtime has hooks, by the engine on each tool call. Without hooks, the post-step check of what changed is the control |
| No push credential | The agent's code host credential is read-only, per step, scoped to the run's repositories and any the role may read. The engine pushes |
| Hidden reference | While the step runs, pushes go to a reference that is not a branch, so no CI runs and no one sees half-finished work. A failed step's commits stay there for a retry |
| `push: false` | Nothing is pushed: scratch work |

---

## 4. Decisions

One mechanism for every human wait (`docs/01` section 7).

| Element | Detail |
|---|---|
| Form | A durable wait in the workflow. No timeout. Reminders on a timer, through notifications |
| Inputs | `type`, `prompt`, `with`, optional `auto`, optional `session` (`docs/03` section 2.3) |
| Outcomes | Declared |
| Idempotency | By decision id. A recorded answer is never asked again |
| Audit | Opened, reminded, answered: with the authenticated actor, the choice and the reason |

### 4.1 Answering

The API authenticates the person, checks organisation policy for the decision
type, records the answer, then delivers it to the workflow. The workflow trusts
the API (`docs/02`). A decision may be answered from chat where policy allows
that type.

### 4.2 Auto-resolution

If the step has `auto: <outcome>` and organisation policy allows
auto-resolution for the type, the decision resolves at once with that outcome,
recorded with actor `policy`. An automatic answer is not a person's signature:
where a later step needs a person's approval (for example a merge into the
default branch, `docs/05`), an automatic answer does not satisfy it.

### 4.3 Interventions

The engine raises an intervention when a route escalates, when a step cannot
answer (section 1), or when a step's allowance runs out and the plan routes it
to escalate. The person answers with a recovery action: retry, go to a step
(optionally with `clean_up`), re-plan at a planning step, grant attempts, or
end the run (`docs/01` section 7.2). The intervention records what stopped the
step, so the answer is informed.

### 4.4 Interactive sessions

A decision with `session` prepares a workspace for a person to work in their
own agent tool (Claude Code, Copilot, an editor) on their own machine: a branch
in a named repository, draft files seeded from consumed artifacts, the context,
the prompt.

| Rule | Detail |
|---|---|
| Reaching it | A CLI and an engine MCP server for the person's agent: fetch the workspace, run checks, submit. Authenticated as the person |
| Pre-submit checks | `checks_from` names later steps; the session may run their checks on demand, with the same task, tools, model and commit as the later step will use. A check never appears for the first time after submission (the prototype's specification session took 8 rounds without this) |
| Submission | The person submits; the engine imports the committed files at that exact commit as the declared artifacts, validates them, and the decision takes the submitted outcome |
| Trust boundary | Hooks and policy do not apply on the person's machine. The engine validates what is submitted, and the person is the accountable actor |

### 4.5 Breakpoints and action requests

A **breakpoint** is set by a person through the API on a step of a running run;
the run pauses before that step and opens a breakpoint decision. An **action
request** is opened by a step that needs a person to act outside the engine
(`scm.merge` with `engine_can_merge: false`); the engine watches for the action
and closes the decision when it sees it, or takes the `declined` path if the
person declines.

---

## 5. Composition

### 5.1 `run.child`

| Element | Detail |
|---|---|
| Form | Child workflow |
| Inputs | `template`, `level`, `repositories`, `parameters`, `run_gate` (`auto` or `manual`, from FF). `manual` starts the child with a breakpoint before its first step, so a person starts it by answering the breakpoint decision |
| Outcomes | From the child's status (`docs/03` section 9.3) |
| Idempotency | By run id and step key: a retry adopts the existing child |
| Checks | The root's work-item budget before creating the child |
| Artifacts | Out: a reference to the child run. The child's artifacts are consumable with `from: { child: <id> }` |

`parallel` and `for_each` are interpreter constructs, not actions (`docs/03`
section 8).

---

## 6. Planning

### 6.1 Planning authority

An agent step with `planning` may call `inspect_plan` and `propose_patch`
(section 3.3). A proposed patch is validated at once and errors go back to the
agent. A valid patch is applied when the step ends with a declared outcome,
unless the step's policy requires review (`planning.review: required`, or the
organisation requires it), in which case it becomes a patch approval decision.
The run then continues at the patch's `resume_at`. A patch to the parent is
sent as a signal (`docs/03` section 9.4).

A planner with tracker tools in its toolset may create tickets for the child
runs it proposes, marking each with the run's label so a later re-plan finds
them (from FF).

### 6.2 `plan.expand`

| Element | Detail |
|---|---|
| Form | Activity: reads the list artifact (at most 500 items), builds the patch, and returns it; the workflow applies it through the patch path. A list from a parameter is expanded locally |
| Inputs | `from` (a list artifact, or a parameter), `shape` (`parallel` or `chain`), `target` (the `parallel` to fill, or the step to chain after), `on_other` (for a chain), `step` (the step to create per item, with `$.<field>` paths into the item) |
| Outcomes | `expanded` |
| Effect | A patch, applied like any other and recorded with `proposed_by: engine` |

---

## 7. Source control

The engine creates a run's work branch in a repository the first time a step
touches that repository, from the run's parent branch, and records the
baseline commit. A run that integrates into the default branch fetches its
start point first, so it never starts from a stale clone (from FF).

### 7.1 `scm.catch_up`

Merges the parent branch into the work branch, so the work is tested against
what it will merge into.

| Element | Detail |
|---|---|
| Inputs | `repository` (default: the step's) |
| Outcomes | `updated`, with output `changed` (true if the work branch did not already contain the parent); `conflict` |
| Notes | A no-op merge is `updated` with `changed: false` (the GitHub 204 case, Spike 3). The standard fragment routes `changed: true` to a re-test and `conflict` to an agent that resolves it, then back through tests and review |

### 7.2 `scm.pull_request`

Opens the work branch's pull request into its parent branch, or updates it.

| Element | Detail |
|---|---|
| Inputs | `title`, `body` (a prompt reference, rendered) |
| Outcomes | `ready`. Output: the pull request reference |
| Idempotency | Marker in the body; a retry adopts the existing pull request |

### 7.3 `scm.merge`

Merges the work branch into its parent branch.

| Element | Detail |
|---|---|
| Inputs | `engine_can_merge` (default `true`), `squash` (default `false`), `require_checks` (default `true`: a passing `ci.checks` result for the exact head being merged must exist), `require_tested` (default `true`: a passing `test.run` result for the exact head being merged must exist). The organisation may lock either (`docs/05` section 3.1) |
| Outcomes | `merged`; `base_moved` (the parent moved since catch-up); `checks_failed`; `declined` (a person asked to merge declined) |
| Idempotency | If the parent already contains the merge, or a person's merge is on the code host, the merge happened: `merged` |
| Limits | Attempts 3 by default, used by executions that end other than `base_moved`. A `base_moved` loop through catch-up re-enters this step and counts against `limits.races` instead (default 10), checked on every execution like any allowance (F4), so a work branch that keeps losing races is still bounded, then escalates. A refusal means a sibling merged, so the loop cannot spin without progress (SP4; LOG 61) |

When the engine merges: it builds the merge commit (or squash commit) from
exactly the head the evidence was recorded for, and pushes it to the parent,
refusing if the parent has moved since that head was caught up. The tested head
is whatever the run's own `test.run` and `ci.checks` evidence names; the
standard fragment re-tests after a catch-up that changed the branch (`docs/08`
section 6.3). With `require_tested` and `require_checks` on, nothing without
evidence can land.
There is no merge lock: work merges the way a team of developers does
(`archive/docs/20`). After a squash, the engine closes the pull request with a
link to the squash commit, because the code host does not show it as merged.

When a person merges (`engine_can_merge: false`, or the code host refuses an
allowed engine merge): the step opens an action request decision (section 4.5),
and returns `merged` when the engine sees the merge on the code host.

Whether the engine may merge into the **default branch** is a safety floor
(`docs/05`). The step re-checks it at merge time, reading the default branch
from the code host. The step records who merged, the method, and the head it
replaced, so later evidence on the squashed branch still counts (from FF).

Where the target requires a code host merge queue, the step enqueues and waits
for the queue's result.

### 7.4 `scm.revert`

Opens a revert pull request for a merge this run tree made. Outcome `opened`.
Merging the revert is a separate `scm.merge`.

---

## 8. Waiting

### 8.1 `ci.checks`

Waits for every check on the pull request's head commit, including checks that
register late.

| Element | Detail |
|---|---|
| Inputs | `expected` (check names that must appear), `grace` (how long to wait for a first check before deciding the repository has no CI; default 2 minutes), `timeout` |
| Outcomes | `passed` (with output `skipped`, the number of skipped checks); `failed` |
| Behaviour | Does not pass while a CI workflow that runs on this pull request has not reported, so an early third-party check does not read as "all passed"; reads the code host's workflow definitions and runs to tell "no CI" from "CI not started" (from FF's `awaitCheck`). A head commit that changes while waiting, or the timeout, raises an intervention |
| Evidence | Check results, which `scm.merge` reads |

### 8.2 `wait.condition`

Waits for an external state, from FF's `awaitCondition`.

| Element | Detail |
|---|---|
| Inputs | One condition: `pull_request` (`approved`, `merged` or `mergeable`), `checks`, or `tracker` (an issue reaching one of some statuses); `poll_every`; `timeout` (default 7 days) |
| Outcomes | `met`; `unmet` (closed unmerged, changes requested, an issue in an `unmet` status) |
| Behaviour | Holds no worker slot. A durable timer polls; an inbound event wakes it early; a read confirms. A person may end the wait by hand with `met` or `unmet`, which is the "skip a step" override with a chosen outcome (`docs/05` section 5). The timeout raises an intervention |

---

## 9. Testing and analysis

### 9.1 `test.run`

Runs a repository's declared test tiers.

| Element | Detail |
|---|---|
| Inputs | `tier_tags` (select tiers by tag; the tiers themselves are repository facts), `baseline` (true: this run records the baseline) |
| Outcomes | `passed`, `failed`, `baseline_failed` |
| Behaviour | Tiers run where the manifest says: in a sandbox, or delegated to CI through the CI provider inside the same step (the default for tiers that need Docker or Windows). Results are parsed from the declared format (TRX, JUnit) into **test results evidence** |
| Baseline | The first `test.run` with `baseline: true` records the baseline. A baseline that is not clean returns `baseline_failed`, because later failures could not be attributed; the standard `baseline` fragment escalates it (from FF) |
| Carried result | If a catch-up left the tree identical to one already tested, the earlier result is adopted and nothing runs (from FF) |
| Cannot answer | No manifest, a tag with no tier, a checkout not at the expected commit, unparseable results: an intervention |

### 9.2 `test.integration`

Starts several repositories as services in a fresh environment, each from its
own manifest's `integration_environment`, and runs their integration tier.
Outcomes `passed`, `failed`. A service that fails to start on two tries in a row
is `failed` (its start command is part of the delivered work); a Docker or
platform failure is infrastructure (from FF).

### 9.3 `analysis.run`

Runs the static analyser the manifest declares (`static_analysis`: `analyser`
of `sarif`, `sonarcloud` or `none`; for `sarif`, the `command` and
`sarif_path`), or reads it from CI (SonarCloud), and compares with the baseline.
Inputs: `baseline`; `fail_on` (`new_issues`, the default, `any_issues`, or
`never`); `min_level` (the lowest severity that counts, default `warning`).
Outcomes `passed`, `failed`.
Produces **analysis results evidence**. No analyser declared: `passed`, with a
note.

---

## 10. Evidence and validation

### 10.1 Evidence types

Artifact types marked `evidence: true` may be produced only by engine actions
(`docs/01` section 5.2). The standard evidence types: test results, analysis
results, check results, merge records, validation reports, trace reports.

### 10.2 Validators

A **validator** is a named, deterministic check over one or more artifacts. It
returns findings with codes and severities, and may offer a report artifact.
Validators are the plug point for any rule a workflow wants checked without an
agent's judgement.

| Kind | Where defined |
|---|---|
| Schema and structure | Library artifact types: JSON Schema for data; required headings and identifiers for Markdown (from FF) |
| Cross-artifact | Validator providers (`docs/06`): code shipped with the engine or by an organisation, selected by name |

The standard set, from FF's validators:

| Validator | Checks |
|---|---|
| `requirement_support` | Every item's `supports` links resolve; every item supports something or gives a reason; every requirement above has a supporter |
| `requirement_ownership` | Every item in a parent-written plan names an owner |
| `requirement_inheritance` | A child's plan holds every item it owns, unchanged in id and links, and none owned by another |
| `verification_method_support` | Every acceptance criterion without an owner has a verification method |
| `verification_satisfaction` | The agent's evidence claims match passing tests in the engine's test results evidence |
| `requirement_trace` | Every requirement of this run traces through owners and supporting items, across completed children, to passing tests at commits the work contains |
| `tests_added` | A fix's claims name at least one test that is new or changed since the commit the finding was raised on |

### 10.3 `artifact.validate`

| Element | Detail |
|---|---|
| Inputs | `validation`: a list of validators, each with `subjects` (consumed artifacts or sets) and `options` |
| Outcomes | `valid`; `invalid` |
| Evidence | A validation report, and any report a validator offers (a trace report) |
| Behaviour | Freezes every repository the run has prepared at its work branch head, so a validator can compare evidence against those commits. A missing validator or unreadable subject raises an intervention. A subject marked `optional` that does not exist is passed to the validator as absent, so `requirement_inheritance` passes for a ticket with no parent plan (LOG 63) |

---

## 11. Findings

The ledger is an engine mechanism; what to do about findings is the
workflow's (`docs/01` section 6, RW4).

### 11.1 `findings.record`

| Element | Detail |
|---|---|
| Inputs | `source`: an artifact of type findings report (from a reviewer agent step), or a findings source provider by name (an external AI review on the pull request, SonarCloud, a SARIF file). `reopen_on`: when a re-raise of a `fixed` or `declined` finding reopens it: `code_change`, `always` or `never`. The engine default, `code_change`, is a fallback; the standard library sets it explicitly |
| Outcomes | `recorded`, with outputs `new`, `reraised`, `reopened`, `repeat_fixes` (findings reopened after being fixed at least twice); `unavailable` (the external source could not review, for example Copilot review not enabled) |
| Behaviour | Fingerprints each finding, matches it against the ledger (including children's), and records it as new or as a re-raise, with whether the code at the fingerprint changed since the finding last changed state. Applies `reopen_on` |

### 11.2 `findings.triage`

| Element | Detail |
|---|---|
| Inputs | `from`: a triage artifact (from a triage agent step or a decision's session) listing, per finding, the new state (`fixed`, `declined`, `escalated`, `open`) and a reason; `post_declines` (default `true`: post each decline reason on the pull request) |
| Outcomes | `applied` |
| Behaviour | Applies each state change with its actor and reason. A triage entry naming an unknown finding fails validation of the triage artifact, which goes back to its producer |

The standard library's fragments build review, triage, fix and verification
from an agent reviewer, these two actions and route guards such as
`findings.open_blocking == 0` (`docs/08`).

---

## 12. Repository, artifacts, tracker, notifications, delivery

| Action | Inputs | Behaviour |
|---|---|---|
| `repo.inspect` | `repository` | Reads the manifest and publishes facts as outputs: tier names and tags, whether CI is configured, the analyser, whether an integration environment is declared, the manifest hash |
| `artifact.compose` | `name`, `members` | Groups artifacts into a set. Copies and approves nothing |
| `artifact.import` | `from`: a session submission, or a repository, folder, files and exact commit | Registers files as artifacts at that commit, validated against their types |
| `artifact.publish` | `repository`, `folder`, `files`, `previous` (an earlier publication to supersede) | Copies artifacts into a repository in one commit and opens a pull request. `conflict` if the destination changed in a way the earlier publication did not expect; nothing is written (from FF) |
| `tracker.update` | `op` (`create`, `update`, `transition`, `link`, `comment`), its fields | One tracker operation. Field mapping is provider configuration. Agents have no tracker write identity; this action and declarative `tracker` on steps are the only write paths, apart from a planner's toolset where policy allows |
| `notify.send` | `audience`, `message` | A notification that needs no answer. A failure retries and never fails the run |
| `delivery.report` | `repositories` | Reads the merge records and states, per repository, whether a merge actually happened. `complete` only if every one merged, so a run redirected past a merge cannot report success (from FF) |

---

## 13. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Should `ci.checks` keep FF's reading of `.github/workflows` to tell "no CI" from "not started"? It is GitHub-specific | Yes, inside the GitHub CI provider, behind a provider method "which CI runs are expected for this head". Other providers answer it their own way |

Judgement calls made in this document are in `docs/LOG.md`, entries 16 to 24.
