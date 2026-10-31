# Feature Flow design review against Feature Factory

Date: 2026-10-31. Scope: `brief.md`, `CONTRIBUTING.md` and `docs/01` to `docs/20`
of feature-flow-poc, compared with Feature Factory's user guide
(`docs/concepts.md`, `docs/step-types.md`) and design notes
(`docs/design/engine-flexibility.md`, `engine-flexibility-2.md`, `issues.md`).
Spike code was not reviewed.

The yardstick is the principle stated in the brief and in `docs/01` section 1.5:
the engine provides facilities (agent steps, engine steps, guardrails,
validation); the workflow provides policy, and a user can set almost anything in
the workflow.

---

## 1. Summary

The durable-execution core is well designed and well evidenced. The plan-as-data
interpreter, the single patch path, error classification, attempt counting on
every execution, the `incomplete` run status, the no-push-credential rule and
the lock-free merge all hold up. Most of them were measured in the spikes.

The problems are above the interpreter, in the workflow surface:

1. **The engine has absorbed workflow policy.** Feature Factory moved process out
   of the engine into the library ("restrictions and process belong in the
   library", engine-flexibility.md). Feature Flow moved it back. The step
   catalogue grew from 3 step kinds plus 11 engine actions to 40 step kinds.
   Several of them hard-code review, triage, fix and delivery policy that the
   brief's principle assigns to the workflow (section 3).
2. **Agents and humans cannot declare their own outcomes.** Feature Factory let
   an agent or human step declare any outcome names (`accepted`, `rejected`,
   `needs_design`). Feature Flow has one fixed engine vocabulary. A judgement
   step can only say `completed`, `failed` or `blocked`, which pushes judgement
   steps into special engine kinds such as `review.run` (section 3.1).
3. **Several Feature Factory facilities have no design in Feature Flow.** The
   most important is requirement traceability (requirements to acceptance
   criteria to engine-parsed test evidence), which the brief lists under "what
   worked and must be kept". Also missing: engine-only evidence artifacts,
   deterministic validators, a general intervention mechanism, agent questions
   (`ask_human`), breakpoints, ratification of exact revisions, and agent access
   to external tools (section 4).
4. **Workflows are verbose and hard for a planner agent to patch.** Every outcome
   of every step must be routed, there is no "next step" default, and escalation
   is a hand-written gate step per level. The single-ticket example is about 270
   lines of YAML (section 5.1).
5. **There are a few incipient defects in the interpreter rules:** the
   continue-as-new safe point may never occur in a wide epic, and
   `replan_parent` counts as a completed branch even when the child has not
   finished its work (section 6).
6. **The documents restate the same rules in many places, and the copies have
   started to disagree** (section 7).

The recommended direction (section 8) is to keep the engine core as designed,
shrink the step catalogue to a small set of mechanisms, restore
workflow-declared outcomes, and move review, triage, fix verification and
delivery into the standard library as templates.

---

## 2. What to keep

These follow the principle and are evidenced. No change suggested.

| Facility | Where | Evidence |
|---|---|---|
| One interpreter workflow; plan is data; continue-as-new by a measured threshold | `docs/01` 3.1, 3.5 | Spike 1: 6.28 events per step |
| One `applyPatch` path for re-plans and steering, with `base_revision` optimistic concurrency | `docs/01` 3.4, `docs/09` 15 | Spike 1 |
| Infrastructure errors retry free; work errors route; unmapped success is a work error | `docs/01` 4.3 | Spike 3 found the GitHub 204 case |
| Every entry to a step counts against its allowance | `docs/01` 3.7 | Spike 3: 213 loop iterations in 15 s without it |
| A run that stops without reaching `{ end: ... }` is `incomplete`, never `completed` | `docs/09` 6.2 | Claude Code proof |
| Agents hold no push credential; engine pushes to a hidden work-in-progress reference | `docs/20` | Design; not yet built |
| Merge without a lock: catch up, re-test if changed, push refused if the target moved | `docs/20` | Spike 3 (with a lock); lock-free form not spiked |
| Repository manifest read per step, never copied into the plan | `docs/01` 5.1.2 | Spike 2 |
| Library lock: every content item pinned by hash at run start | `docs/11` 4 | Equivalent to Feature Factory's frozen run copy |
| Correction turns for a malformed artifact, inside the same agent session | `docs/09` 10.3 | Improvement on Feature Factory |
| Findings ledger as an engine record, with fingerprints that ignore line numbers | `docs/01` 7.2 | Spike 2 measured unstable reviews |

---

## 3. Policy that has moved into the engine

### 3.1 The fixed outcome vocabulary

`docs/06` section 1.1 defines 14 outcomes. Each step kind may return a fixed
subset (`docs/09` section 4.2). `agent.run` may return only `completed`,
`blocked`, `failed`, `attempts_exhausted` and `replan_requested`.

In Feature Factory, agent and human steps declared free outcome names; only
engine actions had fixed sets (`concepts.md`, "Outcomes and routing"). A
reviewer agent returned `accepted` or `rejected`; a design gate could return
`approved`, `rejected` or `needs_research`.

Consequences in Feature Flow:

- A judgement made by an agent cannot be expressed as an agent step. That is
  one reason `review.run`, `findings.triage` and `fix.verify` became engine
  kinds with engine logic.
- A human gate has only `approved` and `rejected`. A third choice needs the
  `choice` field plus guarded routes (`docs/09` 14.1), which is a second
  mechanism for the same thing.
- The vocabulary grows with each new kind: `base_moved`, `findings_open`,
  `ready`, `not_approved` and `any_failed` are each used by one or two kinds.

**Suggestion.** Agent and human steps declare their outcomes, as in Feature
Factory. The engine adds only the outcomes it produces itself
(`attempts_exhausted`, and `replan_requested` when the step returns a patch).
Engine actions keep fixed sets. Validation still requires every declared
outcome to be routed.

### 3.2 Hard-coded review and findings policy

The items below are stated as engine behaviour in `docs/06`. None is needed to
keep the engine safe or its audit trail trustworthy. Each is a sensible default
for a delivery workflow, and some adopters will want a different one.

| Engine rule | Where | Why it is policy |
|---|---|---|
| Every fix must add a regression test; "a step invariant, not a template switch" | `docs/06:923` | A documentation or configuration workflow has no tests to add. The triage-record escape exists because the rule does not fit every case |
| A finding that reappears after being fixed twice escalates | `docs/06:852` | The count and the action are workflow choices |
| A finding is triaged once; "declined" is sticky | `docs/06:840` | Reasonable default; some teams re-triage at epic level |
| A demonstrated bug finding blocks; a reasoned one ranks lower | `docs/06` 8.2, 9.3 | Severity policy |
| The merge bar (decision D8: no unresolved blocking finding, every finding fixed or declined) | `docs/06` 9.3, also `policy.findings.mergeBar` in `docs/07:307` | Stated as an engine rule and as organisation configuration. Neither is the workflow |
| Review angles, machine checks first, evidence tests, different reviewer model | `docs/06` 8.2, `docs/04` 7.1 | Described as properties of the review step and the agent reviewer provider. These are the reviewer's task and role, which are library content |
| `scm.revert` always gates and never merges | `docs/06:613` | Workflow routing |
| `policy.auto_approve` cannot approve a default-branch merge | `docs/06:286` | Already covered by F1 (the default-branch merge setting); a second rule for the same thing |
| A merge conflict goes to an agent, not a human first | `docs/06` 6.2 | Stated as an engine rule; it is only a route in the template |

**Suggestion.** Apply one test to every engine rule: *is it needed for the
engine to be safe, or for the record to be trustworthy, whatever the workflow?*
If yes, it is a guardrail and stays in the engine. If no, it becomes a default
in the standard library. On that test the guardrails are: credentials and
pushing (DR1, DR13), F1, secrets by reference (F2), protected steps (F3),
attempt ceilings (F4), the `incomplete` status, idempotency, and recording who
decided what. Everything in the table above becomes library content.

### 3.3 Approval is the only policy a template may not touch

Rule L12 (`docs/09` 14) says who may approve a decision, including whether
auto-approval applies, is organisation governance and never in a template.
Merging into `main` uses a different pattern: the template sets
`engine_can_merge`, and the organisation caps it (F1).

Feature Factory had one `approvals: human | auto` switch, settable as a run
input so one value covered a whole run tree (`step-types.md`, "Automatic
approval"). A solo developer who wants a fully autonomous run in Feature Flow
must edit organisation configuration.

**Suggestion.** Use the F1 pattern for approval too. The template states what it
wants (for example `auto_approve: true` on a gate); the organisation sets a cap
and who may approve. The cap stays organisation governance; the request moves to
the workflow.

### 3.4 Organisation configuration is keyed by level names

The design says levels are workflow-defined (`docs/20`). Organisation
configuration still keys policy by level name: `policy.approval.ticket`,
`.epic`, `.defaultBranch` (`docs/07:296`), and `merge.integrationBranch`. The
repository file has `branches.integrationPrefix` and `ticketPrefix`
(`docs/07:521`), which assumes two levels.

**Suggestion.** Key organisation policy by decision type pattern
(`*.merge_approval`, `project.*`) and by branch role (default branch, any other
branch), not by level name. Branch naming per level belongs in the template.

---

## 4. Feature Factory facilities with no Feature Flow design

| Facility in Feature Factory | Feature Flow status | Impact |
|---|---|---|
| **Requirement trace.** Verification requirements (VRs) and acceptance criteria (ACs) linked by `supports` and `owner` across child runs; evidence claims mapping ACs to test identities; the engine validates claims against parsed test results; a gap stops the run before merge | Not designed. "Traceability" appears only in the brief. `ticket-plan` has `verification_refs` but nothing checks them | The brief lists this under "what worked and must be kept" |
| **Engine-only evidence.** `test-results`, `static-analysis-results` and `trace-report` may be produced only by engine actions, so an agent cannot assert them | Artifact types exist; there is no rule about who may produce a type | An agent could produce a `test-results` artifact |
| **Validators.** Registered deterministic checks over sets of artifacts (`validateArtifacts`), later declared in the library | Only JSON Schema on one artifact, plus agent review. `checks` is a library content type with no defined schema (`docs/11` 1) | Cross-document rules ("every AC has a verification method") need an agent |
| **Interventions.** `escalate` as a route action; a human answers with retry, goto or redirect | `human.gate` steps with `approved` and `rejected`; the word "intervention" appears only inside `scm.merge` | A human cannot say "retry step X" except through a steering patch |
| **`ask_human`.** An agent asks a question mid-step and waits, keeping its session | Listed as "later, optional" surface D (`docs/01` 7.4) | Agents must stop with `blocked` and lose context |
| **Breakpoints.** Stop before a named step | Not mentioned | Steering covers abort but not pause |
| **Ratification.** A human approves an exact artifact revision; children inherit it as authority | Not designed. `docs/09:1319` cites "a ratified detail" as the reason for E2 | No record of which revision a gate approved |
| **Baselines.** First test and analysis results on the unchanged branch, so later failures are attributed correctly | `analysis.run` has `baseline_ref`; tests have none | Pre-existing failures are blamed on the agent |
| **Run context.** `context.md` carried to later agent steps; `context: none` for an independent reviewer | Not designed | No defined way for step N to see what step N-1 concluded, other than artifacts |
| **Toolsets.** Allow-listed external MCP servers per step (Jira, Confluence) | Roles list built-in tools only; agents have no tracker access (`docs/01` 4.6) | Planners cannot read Confluence or create tickets as Feature Factory's did |
| **Declarative tracker transitions** on any step | Five explicit `tracker.*` step kinds | More steps per template |
| **`after_changes` repository commands** (formatters, generators) run by the engine after an agent commits | Not designed | Formatting drift reaches review |
| **Agent reporting contract.** A preamble prompt and `outcome.json`; "an outcome that claims nothing it promised fails" | `GetResultAsync` returns "outcome, structured output" (`docs/05` 3) but how the agent reports is not specified | The most-used contract in the system is undefined |

Each of these is a mechanism, not a policy, so by the brief's principle they
belong in the engine.

---

## 5. Simplification opportunities

### 5.1 Workflow verbosity

In `docs/13` section 1, `attempts_exhausted` is routed 15 times, almost always
to `gate_escalate`. Each level defines its own escalation gate. `retest` exists
only because `run_tests` routes onward to `review` and a different continuation
was needed. A planner agent writing a patch must supply every route of every
inserted step.

Feature Factory had `continue` (next in list order), `escalate`, and
`on_exhausted` defaulting to `escalate`.

**Suggestion.**

- A plan-level `defaults.routes` map (for example
  `attempts_exhausted: { escalate: true }`), overridable per step.
- An `{ escalate: true }` directive that raises an intervention, with the human
  answering retry, go to a step, re-plan or end. This replaces the per-level
  `gate_escalate` steps.
- Optional `next` (or list order) for `completed`.

These change no interpreter semantics; validation still sees every outcome
routed after defaults are applied.

### 5.2 Forty step kinds

Several kinds duplicate one another or are thin wrappers:

| Kinds | Observation |
|---|---|
| `human.gate`, `delivery.gate`, `policy.auto_approve`, `scm.merge` with `engine_can_merge: false`, `findings.triage` with `mode: human`, pending patch decisions | Six forms of "wait for a human decision". One decision primitive with a type, a prompt and choices would serve all of them |
| `wait.for_state`, `ci.wait_checks` | Feature Factory merged these into `awaitCondition` with providers |
| `test.run_tiers` (which delegates to CI inside the step), `ci.trigger`, `ci.read_results` | Two paths to run a CI tier |
| `delivery.deploy_order` | A pure step whose `order` input duplicates `repositories`; in `docs/13` 2 it serves only as an anchor for the chain expansion |
| `review.run`, `fix.verify`, `findings.triage` | An agent run with a particular role, plus ledger updates (section 5.3) |
| `tracker.*` (5), `artifact.*` (4), `delivery.*` (4) | Thin wrappers; could be one action each with an operation parameter, or declarative step properties as in Feature Factory |

A smaller catalogue of mechanisms, with the standard library supplying
templates and fragments that compose them, would be easier to validate, to
document and for a planner to use.

### 5.3 The review provider runs agents by a second path

`IReviewProvider` (`docs/04` 7) has an "agent reviewer" implementation composed
from the sandbox and agent runtime providers. The reviewer's behaviour (angles,
evidence tests, read-only checkout) is described as provider behaviour. There
are then two ways to run an agent: `agent.run`, and the review provider.

**Suggestion.** Make the standard reviewer an `agent.run` with a reviewer role
(`write_access: none`), a review task, and a `findings` artifact type. Add one
engine action that records findings from an artifact into the ledger. External
reviewers (Copilot review, SonarCloud, SARIF files) become providers behind a
"collect findings" action. Review angles then live in the library, where
`docs/07` section 4 already says they belong.

### 5.4 Library packaging

`docs/11` packages markdown prompts as NuGet packages, with an organisation
overlay directory, and forbids a repository from overriding a task or role.
Feature Factory used a directory (`library.root`) that any user could copy and
edit (`ff library init`).

The lock already gives Feature Flow reproducibility, because it hashes every
item at run start. A directory source would give the same guarantee without a
build pipeline. **Suggestion:** make a directory library first-class for every
profile, keep NuGet as an optional distribution format, and let the solo
profile point at a local folder.

### 5.5 Policy settings spread across layers

The brief's "settings conflicted" lesson asked for one policy model. The merge
into `main` is now governed by: organisation `engineMayMerge` with `locked`;
repository `merge.defaultBranch.engineMayMerge` read from the default branch;
the template's `engine_can_merge`; `policy.approval.defaultBranch`;
`policy.merge.defaultBranch.requireChecks`; `policy.chat.allowedDecisions`; and
code host protection. That is seven settings for one decision, across four
places. Each is defensible; together they reproduce the shape the brief warned
about.

**Suggestion.** A single effective-policy view per merge step, computed at
validation and shown in the plan's readable form, so an author can see the
result without reading four files. Consider removing the repository-level F1
value: the organisation sets the cap, the template requests within it.

---

## 6. Incipient problems

| # | Problem | Detail | Suggestion |
|---|---|---|---|
| 1 | Continue-as-new may never reach a safe point | The loop continues-as-new only when nothing is in flight and no decision is pending (`docs/01:276`). An epic with `max_concurrency: 2` and a gate open for days may never satisfy that. Child workflows have their own histories, which helps, but a long ticket run with an open gate alongside a running step does not | Measure history growth in the worst realistic case (a project run). Design continue-as-new with in-flight children (parent close policy `ABANDON`, re-attach by workflow id) and with open gates carried in state |
| 2 | `replan_parent` counts as a completed branch | `docs/09:402`: a child that asks the parent to re-plan counts as completed for the join, and is not re-run automatically. A ticket that discovered it needs another ticket first has not merged its work. A `join: all` then proceeds to the epic review without it | Treat `replan_parent` as a signal the child sends while it continues or waits, not as its terminal outcome. The branch counts only when the child ends |
| 3 | Unknown decision types fail run start | Check 21 (`docs/12`): a gate's `decision_type` must already exist in organisation `policy.approval`. A library author cannot add a gate without an organisation configuration change | Library declares decision types with a default approval rule; organisation policy overrides by pattern |
| 4 | Templates assume one repository per ticket | `run.repositories[0]` appears 10 times in `docs/13`. `ticket_3` in the epic example has two repositories, which the ticket template cannot handle. The prototype's run was across 3 repositories | Add per-repository fan-out inside a run (Feature Factory had `targetRepo` per step and `relatedRepos`) |
| 5 | No way for a parent to pass values to a child | `docs/13` section 7, questions 1 and 2 are open: the epic contract and the parent branch do not reach the child. Feature Factory had input inheritance by declared name and `consumes ... from: parent` | Design child input binding and parent artifact consumption before the library is written |
| 6 | Routing cannot see repository facts | No `manifest` namespace (`docs/09:536`) and no artifact content in expressions (`docs/09:563`). A template cannot route on "this repository declares an integration tier" or "has no CI" | Let a step publish manifest facts as `outputs`, read in its activity. This keeps the determinism rule |
| 7 | The hook endpoint is in the API service | Every tool call of every agent calls the API (`docs/01` 1.4). An API outage pauses all agents (R19 bounds the damage) | Consider serving hooks from the agent worker or a sandbox sidecar, with the API receiving events asynchronously |
| 8 | Engine-side merges need a working copy | `IVcsProvider.MergeBaseForwardAsync` and `MergeAsync` act on a `RepoRef`, but orchestration workers are stateless. Where the clone lives, and its cost on large repositories, is not stated | State which worker holds the clone and whether it is cached |
| 9 | The lock-free merge is unspiked | `docs/08` R10 says so. Starvation is bounded by the merge allowance of 3, which escalates to a human after three lost races | Spike with three or more concurrent tickets before relying on it |

---

## 7. Inconsistencies between documents

| # | Inconsistency | Where |
|---|---|---|
| 1 | The merge method: `docs/13` 1.1 says `merge_pr` passes only `pr_ref` and the repository manifest names the method, but the same example sets `squash: true`. `docs/11` says `docs/07` applies the "add only" rule to the repository's merge method; `docs/07` says the method is not in the repository file | `docs/13:330`, `docs/11:287`, `docs/07:551` |
| 2 | `policy.approval` keys: `docs/09` says it is keyed by decision type (`ticket.merge_approval`); `docs/07` keys it by level (`ticket`, `epic`, `defaultBranch`); `chat.allowedDecisions` uses a third form (`ticket.gate`) | `docs/09:1072`, `docs/07:296`, `docs/07:323` |
| 3 | Repository facts in step inputs: `test.run_tiers` dropped `tiers` for `tier_tags` because tiers are repository facts, but `analysis.run` still takes `analyser` (its own contract says the analyser comes from `feature-flow.yaml`), and `test.run_composition` still takes `tiers` | `docs/06:711`, `docs/06:730`, `docs/06:738` |
| 4 | `findings.all` and `findings.open` are used throughout the examples; the namespace defines only `blocking_count` and `open_count` | `docs/13` 7 question 3, `docs/09` 8.3 |
| 5 | `docs/01` section 7 lists `run.wait`, and omits `scm.revert`, `tracker.comment` and `notify.send`; `docs/09` and `docs/06` have the current list | `docs/01:1110` |
| 6 | The parent branch: tickets use `run.integration_branch`; epics and projects use a `parent_branch` parameter | `docs/13` 7 question 1 |
| 7 | Mapping child run statuses `partial` and `rejected` to a `run.child` outcome is undefined | `docs/09` 18 (open) |
| 8 | `human.gate` takes `gate_id` and is idempotent by it, but no example sets one | `docs/06` 3.1, `docs/13` |

---

## 8. Documentation maintainability

`docs/09` section 1 states "one fact lives in one place". The documents break
that rule heavily. The commit-and-push behaviour of agent steps (hidden
reference, move the branch once, commit uncommitted changes, `checkpoint`
tool) is described in 13 documents. The F1 paragraph appears in at least 8. The
inconsistencies in section 7 are the result: a change applied to some copies
and not others.

This happened because each review round asked for references to be explained
where they are used, and the explanation grew into a restatement.

**Suggestion.** Keep one owning section per rule. Elsewhere, one sentence that
names the rule and its effect, plus the reference: "Agents never push; the
engine pushes their commits (`docs/20`, commits and pushes)." Check with a
script for repeated paragraphs, as `CONTRIBUTING.md` already does for
cross-references.

---

## 9. Recommended next steps

In priority order. Items 1 to 4 change the workflow language and should be
decided before any library content is written.

| # | Step | Sections |
|---|---|---|
| 1 | Restore workflow-declared outcomes for agent and human steps | 3.1 |
| 2 | Apply the guardrail test to every engine rule; move the failures to standard library defaults | 3.2 |
| 3 | Add route defaults and an `escalate` directive with an intervention answer set (retry, go to, re-plan, end) | 5.1, 4 |
| 4 | Decide parent-to-child data passing and per-repository fan-out | 6 items 4 and 5 |
| 5 | Design requirement trace, engine-only evidence and deterministic validators as engine mechanisms | 4 |
| 6 | Fix `replan_parent` join semantics and the continue-as-new safe point | 6 items 1 and 2 |
| 7 | Reduce the step catalogue; make review, triage and fix verification library templates over `agent.run` and ledger actions | 5.2, 5.3 |
| 8 | Unify the human-decision forms into one primitive | 5.2 |
| 9 | Directory library as a first-class source | 5.4 |
| 10 | De-duplicate the documents and fix the section 7 list | 7, 8 |

## 10. Judgement calls in this review

- Spike code and spike result data were not read; statements about evidence are
  taken from the design documents.
- Feature Factory facilities were taken from its current user guide and design
  notes, not its code.
- The guardrail test in section 3.2 is this review's proposal, not an existing
  project rule.
