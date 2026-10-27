# Review 10: brief 2 second draft

Date: 2026-10-27. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/09`-`docs/14`, and the changes to `docs/06` and `docs/08`, at commit 19b14a6.

## Summary

**Good. Accept with changes.** Every review 09 item is visibly addressed, the commit message maps each one, and the judgement calls are flagged. The engine consistency, routing, artifact contracts, policy references, lock and patch authorisation are now sound.

What remains is mostly in the **examples and the edges between mechanisms**: one privilege escalation path through patches, branch completion semantics, expressions that read artifact content, undefined `run` fields, and two places where the worked examples fail the document's own validation checks. Fix those and brief 2 is done, apart from the spike-gated CEL check.

## A. Must fix

| # | Issue | Change |
|---|---|---|
| A1 | **Privilege escalation through `replace_inputs`.** `agent.run` inputs carry `role`, `tools`, `write_access` and `capabilities`, and the role also defines `write_access`, `tools` and `capabilities` (`docs/09` 11). An agent may not `replace_role` (15.4), but it may `replace_inputs` on any unprotected step, or `insert_after` a new `agent.run`, with `write_access: repository` and any tools. The same setting is also in two places | `write_access`, `tools` and `capabilities` come **only from the role**. A step may **tighten** `write_access` (for example a planner step at `none`), never relax it; validation enforces this. Agent-proposed steps may use only roles that organisation policy allows for re-plans (for example `policy.replan.allowed_roles`); otherwise the patch becomes a pending human decision (E2). Update `docs/06` 2.1 inputs and the readable form (show the effective `write_access`, which keeps review 06 answer 1's "floor visible in the plan") |
| A2 | **A branch's route to `end: completed` ends the whole run.** Directives are run statuses (`docs/09` 6.2), but every `run.child` branch in `docs/13` 2.1 and 3 routes `completed: "end: completed"` | Define branch completion: a branch's terminal outcome returns to its `parallel` join. Either branches have no `routes` for terminal outcomes (the join aggregates), or there is a `join` directive. Say which, and fix the examples |
| A3 | **Expressions read artifact content.** `steps.plan_spec.artifacts.plan.verification.tier_tags`, `steps.delivery_order.artifacts.next` and `steps.triage_findings.artifacts.selected` dereference artifacts, but `docs/09` 8.3 says the namespace holds references only. Reading content in workflow code is I/O and breaks replay | State the rule: expressions see references and outcomes, never content. Where a step needs a value from an artifact, the **consuming step** reads it in its activity (for example `run_tests` consumes the plan artifact and reads `tier_tags` itself). Alternatively, a step may publish a small, typed, size-capped `outputs` map into workflow state, readable as `steps.<id>.outputs.<name>`. Pick one; recommended: `outputs`, capped, declared in the step contract |
| A4 | **`run` and `findings` fields are used but not defined.** `docs/09` 8.3 defines `run.id`, `run.scope`, `run.repositories`, but the examples use `run.findings`, `run.findings.open`, `run.branch`, `run.integration_branch`, `run.head_sha`, `run.aggregate_diff`, `run.tracker_issue`, `run.delivery_ledger`. `findings` is a namespace, but the examples use `run.findings` | Define every field, with its type and where its value comes from. Anything that changes during the run (head SHA, aggregate diff) is a reference resolved by the consuming step, not a value in workflow state |
| A5 | **The worked examples fail the document's own checks.** `apply_child_patch` does not exist (check 11). `verification_review` in `docs/13` 4 is unreachable (check 13). `params.platform_repository` is not declared in the epic template (check 5). The child branches never pass `ticket_id`, which `ticket-delivery` requires (check 6) | Fix them, then run every example through the validation table by hand and say so. Long term, the examples become test fixtures (`docs/12` 3.3) |
| A6 | **Where execution resumes after a re-plan.** `replan` routes `replan_requested: implement` regardless of what the patch did, so a patch that inserts a step before `implement`, or fixes the plan elsewhere, is skipped or ignored | The patch names its resume point (for example `resume_at: <step id>`), validated like a route target. The `plan.replan` route for `replan_requested` then means "continue at the patch's resume point". Define the default if it is omitted |
| A7 | **`insert_before` / `insert_after` on a graph is undefined.** Which routes are rewired? `docs/13` 3 uses `insert_after epic_review` **and** a `change_route` on `epic_review`, which is either redundant or required | Define it: for example, `insert_after X` makes every route of X that pointed at Y point at the new step, and the new step's routes are given explicitly. Or drop the two inserts and keep `add_step` plus `change_route`. Pick the one an LLM gets right more often, and say why |
| A8 | **Mid-run override needs content outside the worker image.** The organisation overlay is "built into the worker image" (`docs/11` 2). The prototype lesson was fixing a broken check prompt and applying it to a running run: with this design, that needs an image build and a worker deploy, and in-flight workers on the old image cannot resolve the new hash | **Decided by the product owner, 2026-10-27:** library content, overlay included, is **published to the artifact store by content hash** when the distribution repository merges. Workers resolve by hash from the lock. An override is then a lock entry plus content that already exists, with no redeploy. Images carry the engine and providers, not content |
| A9 | **Who may make a mid-run override.** It is not stated | Humans only, with the `steerer` or `admin` role, audited. An agent may *propose* one, and it is then a pending human decision, like E2. An override of content used by a protected step is allowed (that is the prototype's fix case), but it is shown as such in the decision request |
| A10 | **The epic findings loop has no fix step.** `epic_triage` routes `completed: epic_review`, so epic findings are triaged and re-reviewed but never fixed | Show the epic fix path: either a fix ticket added by re-plan (a `run.child`), or a fix step on the integration branch. This is the seeded cross-ticket defect case (review 08, answer 4), so it must be in the example |
| A11 | **Delivery across two repositories in order is not shown.** This is a brief deliverable (6b). `deliver` runs once, for `artifacts.next` | Show it. Recommended: the same expansion as tickets. Delivery order is plan data, producing one `delivery.deploy` (or delivery-scope `run.child`, DP3) per repository, chained in order. Answers the `docs/13` open question |

## B. Should fix

| # | Issue | Change |
|---|---|---|
| B1 | Ticket expansion uses an **agent** (`plan.replan`) to turn a typed `ticket-list` artifact into a patch. That is a deterministic transformation, and an LLM adds cost and a failure mode | A deterministic expansion: `plan.replan` with a template mapping (no agent), or a pure engine step that maps each list item to a `run.child` with bound parameters. It still produces a patch, so L9's reasoning holds |
| B2 | Validation check 25 says "blocks" for a patch touching protected content, but E2 says an agent patch becomes a pending human decision | Three results in the table: applied, pending human decision, rejected (floors) |
| B3 | `docs/09` 15.4 reads as rejection ("Yes, if `target` is not protected") | Say "otherwise a pending human decision" |
| B4 | The readable form expands only the current step's routes, but agents patch routes and inputs of other steps | Show every step's non-default routes on one compact line, plus each step's role and effective `write_access`. Inputs stay hidden; an agent asks for a step's detail by id |
| B5 | Producer-side contract failure ends the step `failed` and costs an attempt. A whole agent re-run for a missing field is expensive, and the prototype's agents could usually fix it in place | Return the field-level errors **in the same session**, for a bounded number of correction turns (for example 2). Only then is the outcome `failed` |
| B6 | A task renders `{{ bindings.plan_ref }}`, which is a hash. The agent cannot read an artifact store reference | Consumed artifacts are copied into the sandbox (`CopyInAsync`) and the binding renders a **path** the agent can open |
| B7 | Naming: `artifactTypes`, `roleMapping`, `agentRuntime`, `hardCeiling`, `schemaVersion` are camelCase, against "snake_case everywhere". `docs/07` configuration uses camelCase | State the boundary: plans, patches and library content use snake_case; configuration follows `docs/07`. Or harmonise. Fix the plan and library cases either way |
| B8 | The role reference format is inconsistent: `roles/planner` (`docs/09` 5, `docs/10` 5) versus `feature-flow-default:roles/planner` (`docs/13`). The `LibraryRef` pattern accepts only an exact `@x.y.z`, but `docs/11` 3 allows ranges (`org-acme@>=2.0.0:...`) | One format everywhere; the pattern matches the documented ranges |
| B9 | Directives are magic strings (`"end: completed"`), quoted to survive YAML | Use a mapping: `completed: { end: completed }`. Typed in the schema and needs no quoting. Designer's call; say which |
| B10 | Two repository files: `feature-flow.yaml` and `.feature-flow/library.yaml`. Also unclear whether the run or the repository chooses the template | One repository file: a `library:` section in `feature-flow.yaml`; `.feature-flow/` holds only the content files. The repository names a **default** template; the run may choose another allowed by the organisation. Repository-added skills and checks are resolved into the **lock** at run start, not read per step |
| B11 | The organisation may tighten `write_access` but "may not change `tools`" | It may remove tools (tighten), not add them |
| B12 | The `delivery.gate` example routes `approved` to retry; fix forward and revert are not shown | Keep `approved` / `rejected` as outcomes. The gate records a **choice** (`retry`, `fix_forward`, `revert`), and guarded routes select on it (`steps.delivery_gate.choice == 'revert'`). No new outcomes |

## C. CEL

| # | Issue | Change |
|---|---|---|
| C1 | Cel.NET exists as described (C# port of CEL-Java). Static cost estimation is a **cel-go** feature; it is not clear that CEL-Java or Cel.NET has it | Add "cost estimation exists" to the Spike 1 checks. If it does not, the length and AST-node caps (`docs/09` 8.5) plus capped list sizes in workflow state are enough: CEL is not Turing-complete, so cost is bounded by input size |
| C2 | Cel.NET has a small maintainer base | Note it as a risk with the fallback already stated |

## Answers to open questions

| Question | Answer |
|---|---|
| CEL fallback: a hand-written subset with a published grammar (09, 14) | **Acceptable.** Decided by the product owner, 2026-10-27 |
| `end: partial` and the three delivery choices (09, 13) | B12: approve/reject plus a recorded choice and guarded routes |
| `compatible_with` or structural compatibility (09, 14) | Declared `compatible_with`. Explicit, checkable, and an author decision rather than an accident of shape |
| `run.wait` versus `parallel` (09, 13, 14) | **Decided by the product owner, 2026-10-27:** leave `run.wait` out of the version 1 language unless a worked example needs it. Remove it from the schema enum and mark it reserved in `docs/06` |
| Per-kind input schemas: generated or hand-written (10, 14) | Generated. In implementation, the catalogue becomes one machine-readable file (YAML) that `docs/06` and the schemas are both generated from. Not in scope for this brief; record it as an implementation task |
| `$ref` across documents (10) | Cross-document `$ref` in source; publish a bundled single file |
| `schemaVersion` versus `apiVersion` (10) | Redundant. `apiVersion: feature-flow/v1` is the plan schema version; drop `schemaVersion`, and express a package's `requires.plan_schema_version` as an `apiVersion` |
| Overlay build step (11) | Superseded by A8: a publish step in the distribution repository's pipeline, writing content to the artifact store |
| Package feed (11) | Support nuget.org and an organisation feed in one installation. The solo profile reads a local folder feed (NuGet supports this natively) |
| Per-item versions (11) | No. The hash answers everything a per-item version would |
| Provider script granularity (12) | Yes, script error classes too. And a step can be scripted **per visit** (a list of outcomes), otherwise loops cannot be tested: `review: [findings_open, completed]` |
| `expected_path` with parallel branches (12) | One path per branch plus the parent's path. No interleaving assertions |
| Replay history storage (12) | Histories contain references only (payload codec, `docs/07`). Recorded from fixture repository runs (review 08, answer 3), stored with the engine tests, with a CI check that rejects anything matching the secret scanner |
| One `delivery.deploy` re-entered, or one per repository (13) | One per repository (A11) |
