# Review 02: request for decisions on the remaining open questions

Date: 2026-10-26. From: architecture. To: product owner and review body.
Reviewed: `docs/01-architecture-first-pass.md` and `docs/02-working-questions.md` at commit 3491bd3.

**Direction:** review 01 was feedback from the reviewer. This file is the reverse:
a request for decisions. Each question below has options, a recommendation and
what it blocks. Per `brief.md` section 10, the recommendation is a recommendation;
the decision is yours.

## What is already decided

Review 01 decided Q1-Q6, Q10 and Q13, and settled where the agent loop runs
(pattern C, DP11). Those are applied in the architecture document. Nothing below
reopens them.

## Questions asked here

| # | Question | Blocks |
|---|---|---|
| Q7 | Sandbox definition: devcontainer.json, the engine's own manifest, or both (brief Q3) | The repository manifest and the sandbox provider |
| Q8 | Docker-dependent tests on Kubernetes: privileged pods, sysbox, remote Docker host, or CI delegation (brief Q2) | The sandbox provider and the test step |
| Q9 | Windows-only repositories: can a sandbox provider support Windows hosts (brief Q9) | Worker pools, the test step, and pattern C on Windows |
| Q11 | Concurrency model for several tickets in one repository | The source control steps and the delivery design |
| Q12 | How attempt budgets are counted, and whether steering may exceed the ceiling | The policy model and the steering API |
| Q14 | Resume or retry after a partial delivery across several repositories | The delivery steps |
| Q16 | Cost budgets, and what happens when one is exceeded | The policy model and the cost record |

Two of these are affected by changes made for review 01, and I would like those
changes confirmed or corrected. They are marked **Confirm** below.

Not asked yet: Q15 (multi-tenancy), Q17 (observability contract), Q18 (disaster
recovery), Q19 (manifest changes mid-run), Q20 (interactive human sessions).
These are "Later" in the working list.

---

## Q7. Sandbox definition

The repository manifest (`.feature-flow/manifest.yaml`) already declares test
tiers, setup commands, static analysis, CI mapping and branch conventions. The
question is where the sandbox *environment* comes from.

| Option | Description | Trade-off |
|---|---|---|
| a | `devcontainer.json` only | Reuses a standard many repositories already have. But it describes a developer environment, not a build and test environment: it has no test tiers, no CI mapping, no network policy, no secret references. Many repositories have no `devcontainer.json` at all |
| b | The engine's own manifest only | Full control over everything the engine needs. Duplicates what `devcontainer.json` already says, and diverges from it over time |
| c | Both: the manifest is authoritative, `devcontainer.json` is an optional input | Slightly more resolution logic. Avoids duplication for repositories that already have a `devcontainer.json` |

*Recommendation:* **(c).** The manifest must stay authoritative because it
carries things `devcontainer.json` cannot express. Where a repository has a
`devcontainer.json`, use it as the default for the base image and toolchain, and
let the manifest override. Where both specify the same thing, the manifest wins.

*Design note:* the engine resolves manifest plus `devcontainer.json` into a
**sandbox spec** and passes that to the sandbox provider. The provider never
parses either file. This keeps the provider swappable and keeps the resolution
rules in one place.

---

## Q8. Docker-dependent tests on Kubernetes

`brief.md` D6 already says this is pluggable. The question is which mechanism is
the default, and the security trade-off.

| Option | Description | Trade-off |
|---|---|---|
| a | Privileged pod, Docker-in-Docker | Works everywhere. Grants near-root on the node; a confused or compromised agent escapes the sandbox |
| b | sysbox or another rootless runtime | Much better isolation. Needs a runtime class installed on every node, which not every managed Kubernetes allows |
| c | Remote Docker host or socket proxy | Central and auditable. Shared blast radius across concurrent runs, and it becomes another service to operate |
| d | Delegate the tier to CI | The test runs in exactly the environment CI uses. Costs latency and couples the run to CI availability |

*Recommendation:* **(d) as the default for Docker-dependent tiers**, with (b)
where the platform supports it and (a) only in the solo profile.

Reasons:
- R5, "sandbox does not match CI", was one of the prototype's worst problems. A
  test that deadlocked only on CI runners is precisely the failure delegation
  avoids. Delegation is the only option that guarantees the same environment.
- S12 already says to treat CI delegation as a first-class path, not a fallback.
- It avoids privileged pods in the enterprise profile, which is the profile where
  a node escape matters most.

*Scope:* this is a per-tier decision, not a global one. The manifest declares
which tiers need Docker; a repository with no Docker-dependent tests pays
nothing.

---

## Q9. Windows-only repositories

The first enterprise adopter has .NET Framework repositories that cannot build in
Linux containers (brief section 9). S1 and the `Os[linux, windows]` capability
already anticipate a Windows pool; the question is what the sandbox actually is.

| Option | Description | Trade-off |
|---|---|---|
| a | Windows container | Fits the existing sandbox provider shape. Windows container images are large, the ecosystem is thin, and Windows node pools are expensive and limited |
| b | Windows VM or hosted Windows host as the sandbox | Fits .NET Framework, which often needs a full Windows host with build tools. Heavier to provision and slower to start |
| c | CI delegation for Windows tiers only | No Windows sandbox to operate. The agent cannot iterate on a build failure locally; every attempt is a CI round trip |
| d | Do not support Windows in version 1 | Smallest scope. Excludes the first enterprise adopter's repositories |

*Recommendation:* **(b) as the primary, (c) as the fallback** for tiers that
cannot run on the provisioned host. .NET Framework builds frequently need a full
Windows host with MSBuild or Visual Studio build tools, which is a poor fit for
Windows containers. This is a sandbox provider implementation, not an
architecture change: `Os[windows]` already exists.

**This question has a consequence for pattern C that needs a decision.** Under
pattern C the agent runtime runs *inside* the sandbox, so on a Windows sandbox the
agent runtime must run on Windows. I have not verified that the candidate runtimes
(Claude Code, GitHub Copilot CLI) do so with hook support. If they do not, the
options are: a Windows sandbox with a Linux sidecar for the agent runtime, which
breaks the "one sandbox, one runtime" simplicity of pattern C; or process-level
control only on Windows, with the engine recording the coarser control.

*Recommendation:* treat "agent runtime on Windows with hooks" as a **spike item
before committing to (b)**. It is the one place where pattern C could fail on the
adopter's actual repositories.

---

## Q11. Concurrency model for several tickets in one repository

A11 says several tickets of one epic may run concurrently. Review 01 (F5) added
parallel branches and joins to the plan shape, with `max_concurrency` on the
`parallel` step. What is not settled is how concurrent tickets in *one repository*
avoid conflicting.

| Option | Description | Trade-off |
|---|---|---|
| a | Serialised: one ticket at a time per repository | No conflicts at all. Slow, and contradicts A11 |
| b | Shared integration branch: all tickets merge into it as they go | Conflicts surface early. One broken ticket blocks the others, and per-ticket review diffs become ambiguous |
| c | Per-ticket branches with a merge queue | Standard, scales, each ticket's diff stays clean. Needs a merge queue and re-validation on merge |
| d | Per-ticket branches plus periodic `scm.merge_base_forward` | Keeps branches current so conflicts surface per ticket. Does not by itself serialise the merge |

*Recommendation:* **(c) plus (d).** Per-ticket branches, `scm.merge_base_forward`
to keep them current, and a serialised merge step per repository. R10 already
assumes the serialised merge step. Reject (b): a shared integration branch makes
one failing ticket block the epic, and it makes the epic-level aggregate diff
harder to attribute.

**Confirm (F5 consequence).** I put `max_concurrency` on the `parallel` step,
which makes option (a) a configuration value rather than a separate design: a
repository that must be serialised sets `max_concurrency: 1`. I think that is the
right place for it, because it keeps the interpreter free of repository-specific
logic. Please confirm, or say where it belongs instead.

---

## Q12. Attempt budgets

Section 3.6 (added for review 01, F4) says a resume does not consume a work
attempt, only a restart does. That was my decision and it needs confirming,
because it changes what "attempts exhausted" means.

| Option | Description | Trade-off |
|---|---|---|
| a | Every attempt counts, resume or restart | Simple. Resumability is worthless: a step that resumes three times after infrastructure failures exhausts its budget without ever restarting |
| b | Restarts count, resumes do not | Resumability works. An agent that resumes forever makes no progress and never exhausts anything |
| c | A budget per step kind across the whole run | Catches runaway step kinds. Coarse; a step that legitimately needs many attempts is penalised |
| d | Per step run, and steering may grant more within a hard ceiling | Matches DP6. Needs the hard ceiling defined |

*Recommendation:* **(b) plus (d), with a bound on (b).** A step run has an
attempt ceiling; a restart consumes one, a resume does not. Steering may grant
more within a hard ceiling that no configuration layer may relax, which is
already a safety floor in DP6.

The bound on (b): resumes are free but not unlimited. A resume is free only if it
makes progress — a new commit, or new session output since the last attempt. A
resume that makes no progress counts as a restart. Wall-clock (`start_to_close`)
remains the outer bound. This needs one more definition from you: **what counts as
progress.** I suggest "a new commit on the branch, or a session reference that
differs from the previous attempt's", but the agent runtime provider is better
placed to answer it and I would rather not invent the rule.

---

## Q14. Partial delivery across several repositories

`delivery.deploy_order` produces an ordered plan across repositories. If the run
fails or is retried after some repositories have deployed, the engine must know
what already happened.

| Option | Description | Trade-off |
|---|---|---|
| a | One child workflow per repository, each idempotent; a retry re-enters at the first incomplete repository | Clean resume. Needs a durable record of which repositories are done |
| b | One step with an internal checkpoint list | Fewer moving parts. The checkpoint list is workflow state, which grows and is lost on continue-as-new unless carried |
| c | A delivery ledger in the record store; a retry reads it and resumes | Survives continue-as-new and is auditable. The workflow must read it through an activity, not directly |

*Recommendation:* **(a) plus (c).** DP3 already makes `delivery.deploy` a child
workflow. The delivery ledger belongs in the record store, which review 01 made
the long-term audit record, and the workflow reads it through an activity so
workflow code stays deterministic.

*Question I cannot answer:* does partial delivery need a **compensating action**?
My assumption is no — the engine reports which repositories delivered and stops,
and a human decides. If the adopter expects the engine to roll back, that is a
whole subsystem and A7 ("deploy means trigger and gate an external deployment
system, not perform it") stops being true. Please confirm which.

---

## Q16. Cost budgets

Review 01 (F6) moved this from "Later" to "Should settle" and decided the
recording half: **every step records tokens, model and compute time from the first
version.** Section 5.5 covers it. The enforcement half is open.

| Option | Description | Trade-off |
|---|---|---|
| a | No budgets in version 1, record only | Honest. A run can spend without limit |
| b | Per-run budget: warn at a threshold, gate when exceeded | Preserves "a human decides". Needs a number to set |
| c | Per-step and per-run budgets, hard stop | Predictable spend. A hard stop can abandon a nearly finished run |
| d | Budgets as policy, with the action configurable: warn, gate or abort | Flexible. More policy surface |

*Recommendation:* **(d), but not in version 1.** Record in version 1 as F6
decided; add budgets once there is data to set them against. The prototype's
27-hour run is the only data point and it has no cost record, so any budget set
now is guesswork.

*Two things to settle when budgets do arrive:*
- Is a hard stop ever acceptable, or is a gate always right? I suggest a gate
  always, because it preserves the invariant that a human decides.
- Enforcement needs a running total. A running total is a small scalar, so it can
  live in workflow state without breaking the "references and small summaries
  only" invariant — but it must be updated from activity results, never from a
  clock or a query. Worth stating explicitly when the budget work starts.

---

## Gap: brief Q6 has no decision record

`brief.md` section 10 lists nine questions. Eight are covered: Q1, Q2, Q3, Q4,
Q5, Q7, Q8 and Q9 map to working-list Q1, Q8, Q7, Q4, Q5, Q6, Q9 and Q10/Q13.
**Brief Q6 — "plan-as-data within Temporal limits: history size, continue-as-new,
how re-plans are versioned and audited" — has no question in the working list and
no decision record.**

It is partly answered in the architecture document: section 3.5 (Temporal
constraints), section 3.4 (re-plans and steering share one path), A14 and S14
(interpreter and plan schema versioned separately), and the storage decision
(every plan revision in the artifact store, the current revision in workflow
state).

Deliverable 5 asks for one decision record per open question. Please either
confirm that section 3.5 plus the storage decision closes brief Q6, or tell me
what is still missing and I will write the record.

---

## What I need from this review

1. A decision on Q7, Q8, Q9, Q11, Q12, Q14 and Q16, or a counter-recommendation.
2. Confirmation or correction of the two F5/F4 consequences marked **Confirm**.
3. A rule for what counts as progress on a resumed agent step (Q12).
4. Whether partial delivery needs a compensating action (Q14).
5. Whether brief Q6 is closed by section 3.5, or what is still missing.
6. Whether "agent runtime on Windows with hooks" should be a spike before
   committing to a Windows sandbox (Q9).

Once Q7-Q9 are settled I can write the provider interfaces and the step
catalogue, which is next in the order of work.
