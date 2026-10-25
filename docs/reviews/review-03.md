# Review 03: decisions in response to review 02

Date: 2026-10-26. From: product owner (via a review assistant). To: architecture.
Answers `docs/reviews/review-02.md` (commit cef01cb). Also covers Q11-Q14 detail decided in discussion before review 02 arrived.

## Summary

| Question | Decision |
|---|---|
| Q7 sandbox definition | (c) accepted, and the manifest is renamed and moved: root `feature-flow.yaml` |
| Q8 Docker-dependent tests | (d) CI delegation as the default, (b) where supported, (a) solo only. Accepted |
| Q9 Windows-only repositories | Not in version 1; capability hooks in version 1; spike before committing to (b) |
| Q11 concurrency | (c) plus (d) accepted. Clarification on the integration branch below. `max_concurrency` confirmed |
| Q12 attempt budgets | (b) plus (d) accepted, with the progress rule below |
| Q14 partial delivery | (a) plus (c) accepted. No compensating action. A7 holds |
| Q16 cost budgets | (d), not in version 1. Record only in version 1. When budgets arrive, exceeding one always stops at a gate, never a hard stop |
| Brief Q6 plan-as-data | Closed by section 3.5 plus the storage decision, on condition that the spike measures history size |
| New principle | Engine mechanism vs workflow policy (below) |

---

## Q7. Sandbox definition, and the manifest file

Option (c) accepted: the repository's file is authoritative; `devcontainer.json` is an optional input.

Changes:
- **Rename and move the manifest to a root-level `feature-flow.yaml`.** It describes how automations work with the repository: test tiers and how to run them, setup commands, static analysis, CI mapping, branch conventions, and environment requirements. It applies to every execution target, not only sandboxes.
- The environment can be either **defined inline** (image, toolchain, setup) or **referenced** from an existing `devcontainer.json`. Repositories without a devcontainer need only the one file.
- Other files (Dockerfiles, compose files for integration services) are referenced by relative path from `feature-flow.yaml`.
- **Reserve `.feature-flow/`** for possible later use (for example repository-specific review checklists or workflow content overrides). Do not use it in version 1.

Terminology (replaces "sandbox spec"):
| Term | Meaning |
|---|---|
| `feature-flow.yaml` | The repository file, as written |
| Resolved manifest | `feature-flow.yaml` after merging the referenced `devcontainer.json` and defaults. Produced by the engine; providers never parse either source file |
| Execution target | Where a tier runs: sandbox, local, or CI. A provider receives the resolved manifest and runs the tiers for its target |

## Q8. Docker-dependent tests on Kubernetes

Recommendation accepted: CI delegation is the default for Docker-dependent tiers; sysbox or another rootless runtime where the platform supports it; privileged pods only in the solo profile. Per tier, declared in `feature-flow.yaml`.

## Q9. Windows-only repositories

Required later, not in version 1. Version 1 includes the hooks so support needs no engine change:
- `Os[linux, windows]` sandbox capability; `feature-flow.yaml` can declare `os: windows`.
- Routing to `feature-flow-agent:os-windows` queues (S1).
- A run that needs Windows and has no provider fails at run-start capability negotiation (S3) with a clear message.
- CI delegation of test tiers works for Windows repositories.

Spike accepted: **verify that agent runtimes (Claude Code, GitHub Copilot CLI) run on Windows with hook support** before committing to a Windows sandbox.

Write a short roadmap section:
| Phase | Approach | Notes |
|---|---|---|
| W0 | Spike: agent runtimes and hooks on Windows | Decides whether pattern C holds on Windows |
| W1 | Windows VM sandbox provider (option b): ephemeral or pooled cloud VMs from an image, agent worker on the VM | Primary approach. CI delegation as fallback for tiers the host cannot run |
| W2 | Windows containers on Windows node pools (option a) | Only if density or isolation needs it and the toolchain fits |

Also research before W1: image size and start time, licensing of Windows Server and build tools in VMs and containers.

## Q11. Concurrency in one repository

(c) plus (d) accepted. Clarification, because the terms in review 02 differ from what was intended:

- There is one **epic integration branch** per repository. It is the base of every ticket branch in the epic, and the source of the delivery PR into the default branch.
- Each ticket works on its **own ticket branch**. A ticket branch merges into the integration branch only after its own checks, review and findings bar pass. This is not option (b): tickets do not share a working branch, and a failing ticket blocks no one.
- Merges into the integration branch are serialised per repository by a merge lock. Before merging: `scm.merge_base_forward` into the ticket branch; **rerun checks only if that merge changed the ticket branch**; then merge.
- A merge conflict routes to an agent step that resolves it, not to a human first.
- The planner declares the code areas each ticket expects to touch; the scheduler prefers not to run tickets with overlapping areas at the same time (a preference, not a rule). How the planner does this is workflow content.
- Epic-level review runs on the aggregate diff of the integration branch (DP10), which keeps attribution simple.

**Confirmed:** `max_concurrency` on the `parallel` step.

Code host merge queues (GitHub merge queue, GitLab merge trains):
- Optional `MergeQueue` capability on the code host provider. Only some repositories have it enabled.
- When the target branch requires a queue, the merge step enqueues the PR and waits for the result, because a direct merge is refused. This matters most for delivery merges into a default branch.
- The engine's merge lock remains the default for integration branches.
- CI providers must report results for queue builds (for example the GitHub Actions `merge_group` event).

## Q12. Attempt budgets

(b) plus (d) accepted. Complete rule set:

| Rule | Detail |
|---|---|
| One counting rule | Every entry into a step counts against that step's allowance, whatever routed there (retry, route back, redirect). No second rule set |
| Infrastructure failures are free | Classified infrastructure failures retry with backoff and do not count |
| Resumes are free only with progress | **Progress = a new commit pushed to the branch, or the agent session has grown since the previous attempt** (the agent runtime provider reports session size or a turn count). After **3 consecutive resumes with no progress**, the next one counts as a restart. `start_to_close` remains the outer bound |
| Two limits | Per-step allowance (default by step kind, overridable in the plan) and a per-run ceiling |
| Exhaustion | Outcome `attempts_exhausted`, routed by the plan to a re-plan or a human gate. Never a silent failure |
| Steering grants | Extra attempts up to a hard ceiling (safety floor); beyond it needs the admin role. Always audited |
| Review and fix loops | Governed by the findings ledger, not a raw count. The loop ends when no blocking finding is unresolved (D8). A finding that reappears after being fixed twice escalates instead of looping. A declined finding never restarts the loop |

## Q14. Partial delivery

(a) plus (c) accepted: one idempotent child workflow per repository, and a delivery ledger in the record store read through an activity.

- **No compensating action.** A7 holds: the engine triggers and gates deploys; it does not perform or roll them back.
- On failure the run stops at a delivery gate with status `partial` and three choices: retry, fix forward (route to a fix step or re-plan for the failing repository), or revert (a human decision; the engine opens revert PRs).
- Delivery order comes from plan data.
- Aborting a run does not close its PRs or delete its branches unless explicitly asked.

## Q16. Cost budgets

Accepted: (d), not in version 1. Version 1 records tokens, model and compute time per step (F6). When budgets arrive:
- Exceeding a budget always stops at a gate; never a hard stop.
- The running total lives in workflow state, updated only from activity results.

## Brief Q6. Plan-as-data within Temporal limits

Closed by section 3.5 plus the storage decision (every plan revision in the artifact store, current revision in workflow state), **on condition that the spike measures history size** on a long run with several re-plans and confirms the continue-as-new threshold. Write a short decision record that says this.

## Principle: engine mechanism vs workflow policy

Add to the architecture document. The engine provides mechanisms: steps, routing, gates, providers, policy evaluation and safety floors. What agents do and how delivery is approached (planning rules, feature flagging, deploy ordering, contract compatibility, review checklists) is workflow content defined by the library. When a design question arises, first decide which side it belongs to. Safety floors are the exception: the engine enforces them because no workflow may relax them.

Example: shipping dark behind feature flags is not an engine concern. It depends on whether the adopter has feature flagging (some require every significant change to be flagged). If workflows later need to create or check flags, add an optional feature-flag provider; not in version 1.

## Next

Remaining "Later" questions (Q15, Q17-Q20) will be answered in a further review. Proceed with the provider interfaces and the step catalogue.
