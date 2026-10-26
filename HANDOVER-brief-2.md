# Handover: brief 2, workflow definition language

Written 2026-10-27, at the end of the engine design work and before starting
`docs/briefs/brief-02-workflow-language.md`.

Delete this file once brief 2 is underway and the conventions below are in use.

---

## 1. Where things stand

All six deliverables of `brief.md` (the engine brief) are written and reviewed:

| # | Deliverable | Document |
|---|---|---|
| 1 | Architecture | `docs/01-architecture-first-pass.md` |
| 2 | Provider interfaces and contracts | `docs/04-provider-interfaces.md`, `docs/05-provider-contracts.md` |
| 3 | Configuration schema and profiles | `docs/07-configuration.md` |
| 4 | Step catalogue | `docs/06-step-catalogue.md` |
| 5 | Decision records | `docs/03-decision-records.md` |
| 6 | Risks and spike plan | `docs/08-risks-and-spikes.md` |

Supporting: `docs/02-working-questions.md` (every question decided),
`docs/reviews/review-01.md` through `review-08.md`.

No code yet. This is a design-only repository.

## 2. Next: brief 2

`docs/briefs/brief-02-workflow-language.md`. Twelve questions (L1-L12), seven
deliverables. Read the engine design first — the brief says so, and it means it:
`docs/01` through `docs/06` and the reviews.

Tooling: `tools/watch_remote.py` (see section 3.2) polls `origin` for new reviews.

Three proposals are pre-agreed by the product owner. Confirm them in the design, or
bring evidence against them:

- **L1** one schema: a template is a plan with parameters.
- **L2** routing on outcomes, plus a small deterministic side-effect-free
  expression language (for example CEL). No general scripting.
- **L3** domain-operation patches (insert step after, replace inputs, add parallel
  branch, change route), not generic JSON Patch.

Suggested order: language design document, then the JSON Schema draft, then library
packaging, validation rules, testing, worked examples, decision records.

## 3. Working conventions

These are how the work has been done so far. They are not written down anywhere
else.

### 3.1 Coordinator and child sessions

Work runs in the VS Code agents window, which has orchestration facilities. The
pattern:

- **One agent acts as coordinator.** It does not do all the work itself. It breaks
  the deliverable into pieces, farms them out to child sessions, and monitors and
  controls them.
- **Child sessions are fully interactive.** The user can see what each is doing and
  answer questions in it directly. Do not treat them as fire-and-forget.
- **Run pieces in parallel where they are independent.** For brief 2, the language
  design and the JSON Schema are coupled, but worked examples and decision records
  can often proceed in parallel once the design settles.
- **The coordinator integrates.** Child sessions produce pieces; the coordinator
  resolves conflicts between them, keeps cross-references consistent, and commits.

This applies to coding as well as to this planning cycle.

### 3.2 Monitoring for reviews

Reviews arrive as pushes to `origin` from the product owner, on no fixed schedule.
`tools/watch_remote.py` polls for them:

```powershell
python tools\watch_remote.py                  # one check, then exit
python tools\watch_remote.py --interval 120   # poll every 2 minutes
python tools\watch_remote.py --json           # machine-readable
```

It fetches, reports how far behind the branch is, lists the new commits with their
files, and **inlines the full text of any new file under `docs/reviews/`** so the
coordinator can act immediately. It reads review content with
`git show origin/<branch>:<path>`, so nothing needs to be checked out.

It does **not** pull, merge or rebase — reporting only, so the caller decides.

Intended loop: start it polling in the background, and when a review lands, pull it,
apply it, and continue **without stopping for input** unless a decision is genuinely
needed from the user. Stop only for decisions, not for confirmation.

### 3.3 Review cycle

Work is pushed to `main` on `origin`, and the product owner replies with a review
file in `docs/reviews/`, numbered sequentially. Apply the review, then push. The
cycle has run eight times and works well.

**Commit trailer.** Every commit ends with:

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

**Commit messages.** On Windows, PowerShell mangles commit messages containing
double quotes. Write the message to a file and use `git commit -F <file>`, then
delete it. A `.gitignore` now covers `*.tmp`, so this is safe.

**Beware `git add -A`.** It sweeps up untracked scratch files. It has already
committed a temp file twice and moved a new script onto a scratch branch. Stage
explicitly, or check `git status` before committing.

**Flag judgement calls.** Where a decision was made without being asked, say so
explicitly in the summary rather than letting it look settled. Several of these
have been corrected by review — for example a note claiming Windows tiers fail
capability negotiation, when review 03 had decided CI delegation works for Windows
in version 1.

**Open questions go in the document.** Each deliverable ends with an open questions
section. The product owner answers them in the next review. Do not leave them
implicit.

**Cross-references.** Documents reference each other by section number. Keep them
accurate — check after editing, because a stale section reference is worse than
none.

**Watch for fixes that introduce risks.** Twice now a mitigation has created a new
risk: start-and-poll fixed a lost command but would have inflated history (R20).
When adding a mitigation, ask what it costs.

## 4. Things worth not losing

- **Engine mechanism vs workflow policy** (`docs/01` section 1.5). The engine
  provides mechanisms; workflow content provides policy and practice. Safety floors
  are the exception — the engine enforces them because no workflow may relax them.
  This principle decides several of L1-L12.
- **Events wake, reads confirm** (`docs/04` section 1.3.1). An event is never a
  source of truth, only a hint that something may have changed.
- **Repository facts are read per step, never copied into the plan** (`docs/01`
  section 5.1.2). The prototype went stale exactly this way. Brief 2 lesson 3 is
  the same point for protected steps holding copied values.
- **Brief Q6 is provisional.** It closes only when Spike 1 measures history size
  (`docs/08` section 2.1, exit criterion 5).
- **The plan schema is versioned separately from the interpreter** (S14). Brief 2
  is designing the thing that gets versioned.
