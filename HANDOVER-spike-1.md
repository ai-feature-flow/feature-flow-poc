# Handover: Spike 1, interpreter under failure

Written 2026-10-27, at the end of brief 2 and before starting Spike 1.

Replaces `HANDOVER-brief-2.md`, which is now stale: brief 2 is complete and its
conventions are in use. Delete this file once Spike 1 is underway and the
conventions below are in use.

---

## 1. Where things stand

**Brief 2 is complete.** Review 11 said "small changes, then brief 2 is complete"
and those are applied and pushed (`ffa2e9e`). All of L1-L12 is designed and
decided.

| Deliverable | Document |
|---|---|
| Language design | `docs/09-workflow-language.md` |
| JSON Schema draft | `docs/10-workflow-schema.md` |
| Library packaging and versioning | `docs/11-library-and-versioning.md` |
| Validation rules and testing | `docs/12-validation-and-testing.md` |
| Worked examples | `docs/13-workflow-examples.md` |
| Decision records L1-L12 | `docs/14-workflow-decision-records.md` |

Engine documents changed by brief 2: `docs/06` (step inputs and contracts),
`docs/08` (spike measurements and exit criteria).

**No code yet.** This is still a design-only repository. Spike 1 is the first
code.

## 2. Next: Spike 1

`docs/08` section 2.1 is the spec. Decided by the product owner, 2026-10-27
(review 11 section E). It gates the other spikes.

| Item | Detail |
|---|---|
| Scope | As specified in `docs/08` 2.1: the failure injections, the six measurements, and all nine exit criteria |
| Also in scope | The Cel.NET checks (`docs/09` 8.1): restricted checker, replay determinism, cost estimation, cost limit. And the `insert_after` measurement (review 11 C2) if the agent runtime part is ready; otherwise Spike 2 |
| Code location | `spikes/01-interpreter/`. **Spike code, not production code**: correctness of the measurements matters, polish does not |
| Infrastructure | Local Temporal dev server and fake providers first. **Ask before adding anything that needs credentials or cloud resources** |
| Output | `docs/15-spike-1-results.md`: measurements, exit criteria met or not, and which provisional decisions close or change |
| Reviews | Push results as they become meaningful, not only at the end. The same review cycle applies |

**Read first:** `docs/08` 2.1 (the spec), `docs/01` section 3 (the interpreter),
`docs/09` (the plan schema the interpreter consumes), `docs/06` (step contracts),
`docs/05` 1.2.1 (poll inside one activity).

### 2.1 The two provisional decisions Spike 1 closes

| Decision | Where | Closes on |
|---|---|---|
| **Brief Q6**, plan-as-data within Temporal limits | `docs/03` Q6 | Exit criterion 5: history size recorded and the continue-as-new threshold set from it. Until measured, that record stays provisional |
| **CEL implementation** | `docs/09` 8.1 | The four Cel.NET checks. If any fails, the fallback is a hand-written subset with a published grammar — already accepted by the product owner |

Both are flagged as provisional in their documents. Say explicitly in
`docs/15-spike-1-results.md` which one closed and which changed.

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
- **Run pieces in parallel where they are independent.**
- **The coordinator integrates.** Child sessions produce pieces; the coordinator
  resolves conflicts between them, keeps cross-references consistent, and commits.

This applies to coding as well as to planning.

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

**Start it polling in the background at the start of the session.** A one-shot
check finds nothing unless a review happens to have landed already; that is how
review 10 was missed for 49 minutes.

Intended loop: start it polling, and when a review lands, pull it, apply it, and
continue **without stopping for input** unless a decision is genuinely needed from
the user. Stop only for decisions, not for confirmation.

**Known bug, fixed 2026-10-27:** `print_report` wrote review text to stdout, and
Python on Windows defaults to cp1252. Review text contains characters cp1252
cannot encode, so it raised `UnicodeEncodeError` — *after* the fetch had already
advanced `origin/<branch>`. The tool reported nothing while the remote ref had
silently moved. `main()` now reconfigures stdout and stderr to UTF-8. If output
ever looks empty when a review is known to exist, check this first.

### 3.3 Review cycle

Work is pushed to `main` on `origin`, and the product owner replies with a review
file in `docs/reviews/`, numbered sequentially. Apply the review, then push. The
cycle has run eleven times and works well.

**Commit trailer.** Every commit ends with:

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

**Commit messages.** On Windows, PowerShell mangles commit messages containing
double quotes. Write the message to a file and use `git commit -F <file>`, then
delete it. A `.gitignore` covers `*.tmp`, so this is safe.

**Beware `git add -A`.** It sweeps up untracked scratch files. It has already
committed a temp file twice and moved a new script onto a scratch branch. Stage
explicitly, or check `git status` before committing.

**Flag judgement calls.** Where a decision was made without being asked, say so
explicitly in the summary rather than letting it look settled. Several of these
have been corrected by review. The product owner reads these and responds to them.

**Open questions go in the document.** Each deliverable ends with an open questions
section. The product owner answers them in the next review. Do not leave them
implicit. When every question is answered, say "None open" and list the answers —
do not leave a stale list.

**Cross-references.** Documents reference each other by section number. Keep them
accurate — check after editing, because a stale section reference is worse than
none.

**Watch for fixes that introduce risks.** Three times now a mitigation has created
a new risk: start-and-poll fixed a lost command but would have inflated history
(R20); polling inside one activity fixed that but holds a worker slot for hours
(H1). When adding a mitigation, ask what it costs.

**Grep for superseded forms before pushing.** Review 11 asked for this and it
caught three stale-text rows. After changing a name, a directive form or an input,
grep every document for the old form. Prose quoting the old form to explain a
change is fine; live usage is not.

### 3.4 Verify, do not assert

The review board checks claims. Twice now a document asserted that its own
examples passed its own validation, and they did not.

- Write a throwaway script to check examples against the validation table, then
  delete it. This caught: invalid YAML on unquoted `end: completed` directives;
  `mode: expand` steps missing `resume_at`; a route target that did not exist; an
  unreachable step; and a `delivery` step renamed to `delivery_start` with a route
  left pointing at the old id.
- Parse every JSON schema block before committing.
- Say in the commit message what was verified and how.

## 4. Things worth not losing

- **Engine mechanism vs workflow policy** (`docs/01` 1.5). The engine provides
  mechanisms; workflow content provides policy and practice. Safety floors are the
  exception — the engine enforces them because no workflow may relax them.
- **Events wake, reads confirm** (`docs/04` 1.3.1). An event is never a source of
  truth, only a hint that something may have changed.
- **Repository facts are read per step, never copied into the plan** (`docs/01`
  5.1.2). The prototype went stale exactly this way.
- **An interruption must become a continuation, not a wrong outcome.** R11, R18
  and R19 are the same class, and it was the prototype's defining failure. Spike 1
  exists to prove the design handles it.
- **Privilege comes from the role, never from the step** (`docs/09` 11.0). An agent
  may not grant itself `write_access`, `tools` or `capabilities` through a patch.
- **A patch has three results, not two**: applied, pending human decision,
  rejected. An agent patch touching protected content becomes a pending human
  decision; only a floor violation is rejected outright.
- **A `run.child` branch routes nothing.** `replan_parent` is handled by the
  interpreter, not routed to an agent `replan` step, which would discard the
  child's patch.
- **The plan schema is versioned separately from the interpreter** (S14).

## 5. Open items carried into Spike 1

| Item | Where |
|---|---|
| The `outputs` cap of 16 entries and 256 bytes is a starting value | Spike 2 records actual sizes per kind (review 11 C1) |
| `insert_after` rewiring is argued, not measured | Spike 1 or 2: planner agent authors patches with both forms, record validation failure rates (review 11 C2) |
| Cel.NET's small maintainer base | Recorded as a risk with the fallback stated (`docs/09` 8.1) |
| `run.wait` | Reserved in `docs/06`, out of the version 1 language. Returns only if a worked example needs it |
