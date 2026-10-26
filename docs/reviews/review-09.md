# Review 09: brief 2 first draft — rework required

Date: 2026-10-27. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/09`-`docs/14` at commit 8643fb7.

## Summary

**Rework, not revision.** The draft confirms L1-L3 but does not integrate with the engine design (`docs/01`, `docs/06`, `docs/07`) or with recorded decisions. The examples break several of them. L6 (artifact contracts), L9 (child plans) and L12 (policy references) are asserted in decision records but not designed. Treat this review as the checklist for the second draft.

Process: the coordinator must cross-check every step kind, outcome, input name and example against `docs/06` and every recorded decision before pushing, and flag judgement calls (`HANDOVER-brief-2.md` section 3.3).

## How to proceed

The model used for this draft has been corrected. **Rewrite `docs/09`-`docs/14` from the brief**, using this review as the checklist, rather than patching the current text. Re-read `docs/01`-`07` and reviews 01-08 first. Keep L1-L3 as confirmed. Each issue below should be visibly addressed in the second draft; say in the commit message where each one landed.

## A. Consistency with the engine design

| # | Issue | Change |
|---|---|---|
| A1 | Step kind enum (13 kinds) does not match the catalogue. `scm.checkout`, `scm.commit` and `tracker.comment` do not exist. `review.run`, `run.child`, `run.wait`, `ci.*`, `findings.*`, `scm.open_pr`, `policy.auto_approve`, `wait.for_state`, `artifact.*` and `plan.validate` are missing | Generate the enum from `docs/06`. One source of truth; say which document owns it |
| A2 | Outcome vocabulary omits `any_failed` (aggregate outcome of `parallel` and `run.wait`) | Use the `docs/06` section 1.1 vocabulary plus `any_failed`. Each step kind declares which outcomes it can return |
| A3 | `join: any` is "first successful branch"; `docs/01` 3.3.1 says first terminal outcome, cancelling the rest. `join: n` is missing | Follow `docs/01` 3.3.1 |
| A4 | Step fields differ from `docs/01` 3.3 (`routes`, `limits`, `gate` there; `outcomes`, `timeout`, `attempts` here). Input names are camelCase here, snake_case in `docs/06` (`max_concurrency`, `context_refs`, `pr_ref`) | One field set, one naming convention, matching the catalogue. If you change the engine docs, say so and update them |
| A5 | Parallel branches carry `planRef` directly. In `docs/01` a branch is a step or sub-plan, and a ticket branch is a `run.child` | Ticket branches are `run.child` steps with `plan_ref`; show it |

## B. Routing

| # | Issue | Change |
|---|---|---|
| B1 | Routing is defined twice: a step `outcomes` map and a top-level `routes` list, and the examples use both, sometimes inconsistently | One form. Recommended: per-step `routes` (as `docs/01`), with optional ordered guards (`when`) per outcome and a required unguarded fallback |
| B2 | Route targets that are not step ids: `human.gate`, `completed`, `rejected`. Step ids equal to kind names (`test.run_tiers`, `fix.verify`) | Define **terminal directives** (for example `end: completed`, `end: failed`) distinct from step ids. Every target is a step id or a directive. Step ids must not look like kinds |
| B3 | The test→fix→test loop has no bound in the plan, and nothing says how loops interact with attempts | State that loops are bounded by the engine: per-step and per-run attempts and the findings-ledger rule (D20: escalate when a finding reappears after two fixes), surfaced as `attempts_exhausted`. Validation requires every cycle to have a route out |

## C. Examples that break recorded decisions

| # | Issue | Decision broken | Change |
|---|---|---|---|
| C1 | `test.run_tiers` input `tiers: [unit, integration]` | Repository facts come from `feature-flow.yaml`, read per step, never copied into the plan (review 03, review 04 Q19) | Tiers come from the manifest; the step may select by tag, not list them |
| C2 | `scm.merge` input `strategy: squash` | Org states allowed merge methods, repo chooses within them (review 07) | No merge method in templates |
| C3 | Parameter `repo` defaults to `service`; delivery lists `[service, platform]` | Templates refer to capabilities and roles, never installation-specific names (brief lesson 5) | No default repository names |
| C4 | `fix.verify` with `allowRegression: true` | Fix verification requires a regression test (review 08, K4) | Remove |
| C5 | Epic review is an `agent.run` returning `ready` / `findings_open` | Review is `review.run` feeding the findings ledger; `ready` means the merge bar is met (D8) | Use `review.run`, `findings.triage`, `findings.evaluate_readiness` |
| C6 | Delivery routes `partial` to `completed` | No silent success (`docs/06` section 1.1) | Route `partial` to a gate or a re-plan |
| C7 | Human gate inputs `requiredRole`, `minApprovers` | L12: no policy in templates | The gate names a decision type; who may approve comes from the policy model |
| C8 | Role example hard-codes `claude-sonnet` and `claude-code` | Runtimes and models are provider instances selected by role (review 07 Q3) | Roles reference a runtime capability or named instance through role mapping in the org layer |
| C9 | Human session `checks: [merge-bar, security-scan, policy-approval]` and a literal `sessionId` | Pre-submit checks are the validators of later gated steps, offered on demand (D23, `docs/06` 3.2) | Derive offered checks from the plan's later steps; ids are engine-generated |

## D. Missing or thin designs

| # | Item | What is needed |
|---|---|---|
| D1 | **L6 artifact contracts** | Absent. Needed: how an artifact type and its JSON Schema are declared in the library; how a step declares produced and consumed types; producer-side validation with field-level errors returned to the agent for a retry; consumer compatibility checked at load. One worked example (for example a ticket plan artifact) |
| D2 | **L9 child plans** | The epic example hard-codes three ticket ids although `plan-tickets` generates the tickets. Show the mechanism: the planner produces a ticket-list artifact, then either a patch adding `run.child` branches or a `parallel` whose branches are bound from that artifact. Say which, and how a child re-plan (`replan_parent`) reaches the parent |
| D3 | **L12 policy references** | One snippet. Needed: what a gate refers to (decision type), what the policy model resolves (who may approve, chat-allowed, auto-approve), and where it lives (org layer, `docs/07` 1.1) |
| D4 | **L4 roles** | Add tools allowed, `write_access` values from `docs/06` 2.2, runtime selection via role mapping. Say what the org layer may change about a library role |
| D5 | **L5 binding** | How parameters reach a task (template engine? which syntax?), what is rendered, where the rendered prompt is stored (content hash) |
| D6 | **Expression language** | Name it: real CEL (with a .NET implementation named and checked) or a defined subset with a grammar. Define the variable namespaces (`params`, step outputs, run state); `facts` and `repo` are used but undefined. Drop `${...}` interpolation as a second syntax, or define exactly how it relates. Remove "loops" from the scope list (none exist). Bound evaluation cost |
| D7 | **Readable form for planners** | Brief lesson 2: planners read a readable form and submit small patches. Define the rendering an agent sees (for example compact YAML with step ids and routes only) |
| D8 | **L7 packaging** | The brief asks for NuGet-style packaging or a justified alternative; not addressed. Two different package manifests (sections 2 and 10 of `docs/11`). Three reference formats (`roles/implementer`, `lib/tasks/x`, `lib/tasks/x@1.2.0`). Pick one format; resolve versions to a **lock** at run start |
| D9 | **Repository-level library overrides** | `docs/11` section 3 adds a "repository overrides" layer for library content, which was never decided and conflicts with `docs/07` 1.1 (repositories may not set role mapping or policy). **Decided by the product owner, 2026-10-27:** a repository may choose a template and **add** its own skills and checks. It may not override organisation roles, policies or tasks. The effective library is base, then organisation, then run overrides (L8), plus repository additions |

## E. Patches

| # | Issue | Change |
|---|---|---|
| E1 | `change_route` can bypass a protected step without removing it | Define "weaken": removing, bypassing (making unreachable), changing inputs, routes out, limits or role of a protected step. All blocked |
| E2 | Brief lesson 3: a patch that touches ratified content **routes to a human**. The draft only rejects | **Decided by the product owner, 2026-10-27:** an agent patch touching protected content becomes a pending human decision; a human may approve it with a reason. Safety floors stay absolute: no patch, human or agent, relaxes them |
| E3 | `planVersion` meaning unclear | `base_revision`: optimistic concurrency. A patch against a stale revision is rejected and re-proposed |
| E4 | The schema's `Patch` def is a single operation; the document form (`changes`, metadata) is not in the schema. `patches` inside the plan schema contradicts the design note | Separate patch document schema; remove `patches` from the plan |
| E5 | `override_library_reference` duplicates mid-run override (section 9); `insert_policy_guard`, `set_parameter` undefined | One mechanism for mid-run override (recommended: a separate audited operation, not a plan patch). Define or remove the others |
| E6 | Who may use which operation | State which operations agents may propose and which only humans may apply |
| E7 | The re-plan example targets `delivery.sync`, which does not exist, and is described as a ticket plan | Use the `docs/01` section 7 scenario: ticket returns `replan_parent` with a patch adding a `run.child` for the second repository to the epic plan |

## F. Validation

| # | Issue | Change |
|---|---|---|
| F1 | "Unresolved `${...}`" checked at template load, where parameters are unbound by definition | Unbound references block at binding (run start); undeclared references block at load |
| F2 | The blocking-versus-warning **rule** is not stated (brief lesson 7) | State the rule. Recommended: anything that can change routing or execution blocks (unreachable step, cycle without exit, unrouted outcome, contract mismatch, type error in an expression); warnings are cosmetic only, and are listed in the run start record |
| F3 | Missing checks | Unreachable steps; every outcome a kind can return is routed; cycles have an exit; producer/consumer contract compatibility; expression type checks; protected-step reachability |

## G. Testing

| # | Issue | Change |
|---|---|---|
| G1 | "Replay a recorded run against a changed template" conflates Temporal replay (same code, same history) with re-driving a run using recorded provider responses | Separate them: Temporal replay tests for interpreter changes; recording providers re-drive a changed template |
| G2 | Test file format is three loose examples | Define the format: template, bound parameters, fake provider script (outcome per step id), expected path of step ids, expected validation errors |

## H. Carried from review 08

| # | Issue | Change |
|---|---|---|
| H1 | A poll loop inside one long-running activity holds a worker slot for the whole step, possibly hours | Add worker slot capacity to the Spike 1 measurements and to sizing guidance |
| H2 | If Temporal is unavailable longer than the heartbeat timeout, the activity times out | State that the retry re-attaches by handle and the step does not fail (K2); test it in Spike 1 |
| H3 | `wait.for_state` now polls inside an activity, but `docs/05` 1.2.1 says idle waits (human-driven) use the durable-timer form | State which form `wait.for_state` uses when |

## Answers to open questions

| Question | Answer |
|---|---|
| Collection comprehensions in expressions (09) | No. Predicates and selection only (`all`, `any`, `exists`, `size`) |
| Inline route guards vs top-level (09, 10, 13) | Per-step routes with ordered guards (B1) |
| `defaultRoute` in library templates (09) | No implicit routes. Every returnable outcome is routed or the template fails validation; a terminal directive is explicit |
| First-class `plan.replan` step (09) | Yes; it exists in `docs/06` 5.1. Steering stays an API path; both use `applyPatch` |
| Richer input types (10) | Yes: inputs are typed per step kind, from the catalogue |
| Reference format (10) | One format (D8); designer's choice, justified |
| Tasks and checks in separate packages (11) | One package type holding all content types |
| Run override depending on a newer version (11) | Allowed if it is in the distribution repository; recorded with old and new hash |
| Minimum engine version (11) | Yes: a package declares the plan schema version and minimum engine version it needs; checked at load |
| Where workflow tests live (12) | With the library package they test |
| Golden patch diffs (12) | Functional expectations (resulting path and validation result); golden only for the readable form (D7) |
| Runtime warnings from templates (12) | No; static only (F2) |
| Same template reused across branches (13) | Yes; that is the normal ticket case |
| Patterns needing `any` join (13) | Keep `all`, `any`, `n` as `docs/01` defines |
