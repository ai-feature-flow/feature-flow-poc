# Handover

Written 2026-10-26, immediately before renaming the working directory from
`d:\Dev\feature-flow` to `d:\Dev\feature-flow-poc`.

Purpose: the rename closes the VS Code session. This file carries the state
needed to resume in the reopened folder.

---

## 1. Immediate actions after reopening

1. Open folder `d:\Dev\feature-flow-poc` in VS Code.
2. Confirm git still resolves: `git status -sb`, `git remote -v`.
   The `.git` directory moves with the folder, so history and the remote are unaffected.
3. Push the one local-only commit: `git push origin main`.
4. Delete this file once the rename is confirmed and the push has succeeded.

## 2. Repository state at time of writing

| Item | Value |
|---|---|
| Path (before rename) | `d:\Dev\feature-flow` |
| Path (after rename) | `d:\Dev\feature-flow-poc` |
| Remote | `https://github.com/ai-feature-flow/feature-flow-poc.git` |
| Branch | `main`, tracking `origin/main` |
| Local vs remote | ahead by 1 commit |

```
f7e4888 (HEAD -> main) Adopt Feature Flow as the application name   <- local only, needs push
b414944 (origin/main)  Add design brief and first-pass architecture  <- pushed
0e4825a                Initial commit                               <- pushed (LICENSE only)
```

Git identity in use: `sparkz-nz <stuart.parkinson.nz@gmail.com>` (global config).

## 3. Files

| File | Status |
|---|---|
| `brief.md` | Input. The design brief. Not authored here; treat as the source of truth for requirements |
| `docs/01-architecture-first-pass.md` | First-pass architecture. Draft |
| `docs/02-working-questions.md` | Assumptions, suggestions, discussion points, open questions. Draft |
| `HANDOVER.md` | This file. Temporary |
| `LICENSE` | MIT, (c) 2026 ai-feature-flow. Came from the remote's initial commit |

No `.gitignore` yet. Add one before any code lands.

## 4. Naming decision (settled)

Application name is **Feature Flow**. In code, configuration, paths and package
names: `feature-flow`.

Consequences already applied in the docs:
- Repository manifest path: `.feature-flow/manifest.yaml`
- Temporal task queues prefixed: `feature-flow-orchestration`, `feature-flow-agent`, `feature-flow-agent:<capability>`

Still to decide, should follow the same prefix: Temporal namespace naming,
package and assembly names, CLI name, console title.

This repository is `feature-flow-poc`: a proof of concept that may evolve into
the finished product or be rewritten. The `-poc` suffix is deliberate and
temporary.

## 5. Where the design stands

Deliverables from `brief.md` section 11, and their state:

| # | Deliverable | State |
|---|---|---|
| 1 | Architecture document | First pass written. Needs review and a decision on Q1 before it is final |
| 2 | Provider interface definitions | Not started. Sketched in section 4 of the architecture doc |
| 3 | Configuration schema, precedence, policy model, three profiles | Partially sketched. Profiles table exists; schema not written |
| 4 | Engine step catalogue with full contracts | Families listed; individual contracts not written |
| 5 | Decision record per open question | Not started |
| 6 | Risks and spike plan | Risks listed (R1-R10). Spike plan not written |

## 6. The blocking question

**Q1: implementation language, .NET or Python.** Everything downstream waits on it.

Recommendation recorded in `docs/02-working-questions.md` (DP2): **.NET**, on the
grounds that the first enterprise adopter has Windows-only .NET Framework
repositories, and static typing helps across a large provider surface. The
counter-argument to weigh: if the project's centre of gravity is agent SDKs and
prompt tooling, Python's ecosystem may matter more.

## 7. Suggested order of work

1. Settle Q1 (language).
2. Settle the storage model: Q2 (artifact store semantics), Q3 (record store
   contents and query model), Q10 (Git vs object store vs database).
3. Settle the control plane and identity: Q4 (signals vs updates, API auth),
   Q5 (agent and engine identity per profile).
4. Settle Q6 (provider packaging: in-process, out-of-process, or both).
5. Write the provider interfaces and the step catalogue.
6. Settle the sandbox and test story: Q7 (sandbox definition), Q8 (Docker on
   Kubernetes), Q9 (Windows hosts).
7. Write the configuration schema and the three profiles.
8. Write decision records for the brief's Q1-Q9.
9. Plan the spike: an interpreter workflow that survives a worker crash and
   applies a mid-run re-plan.

## 8. Design decisions worth not losing

These are in the architecture doc, but they are the load-bearing ones:

- **One interpreter workflow for all run scopes.** Ticket, epic and delivery
  differ by plan data, not by code.
- **Everything outside `await` in the interpreter is a pure function** of plan
  and state. That is the determinism boundary.
- **Re-plans and steering share one path**: both produce a plan patch, both go
  through a single `applyPatch` that enforces protected steps.
- **Workflow state holds references and small summaries only.** No transcripts,
  diffs, code or secrets. Full bodies live in the record store.
- **Providers classify their own errors** as infrastructure (retry, no attempt
  consumed) or work (route by outcome). Steps may override.
- **Configuration precedence has safety floors** that no lower layer may relax:
  human approval for default-branch merges, secrets by reference, protected
  steps, attempt ceilings. This is the fix for the prototype's "settings
  conflicted" failure.
- **Findings are fingerprinted on rule, repo, path, symbol and normalised
  snippet. Never on line numbers.**
- **Epic-level review uses the aggregate diff**, not a roll-up of per-ticket
  findings. The prototype's cross-ticket findings were only visible across
  tickets.

## 9. Open question numbering

`docs/02-working-questions.md` uses four prefixes: **A** (assumption),
**S** (suggestion), **DP** (discussion point, with a recommendation),
**Q** (open question). DP1 is closed (name settled). All other numbering is
stable; keep it stable in future documents so references do not break.
