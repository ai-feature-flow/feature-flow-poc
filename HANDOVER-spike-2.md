# Handover: Spike 2, one real ticket run

Written 2026-10-28, at the end of Spike 1 and before starting Spike 2.

Replaces `HANDOVER-spike-1.md`, which is now stale: Spike 1 is closed. Delete
this file once Spike 2 is underway and the conventions below are in use.

---

## 1. Where things stand

**Spike 1 is closed.** Review 13 said "one measurement is invalid; fix it and
Spike 1 closes", and that is applied and pushed. All nine exit criteria are met.
Results are in `docs/15-spike-1-results.md`.

| Deliverable | Document |
|---|---|
| Spike 1 results | `docs/15-spike-1-results.md` |
| Spike code | `spikes/01-interpreter/` |
| Replay fixtures (the engine's first replay suite, S8) | `spikes/01-interpreter/tests/histories/` |

**Both provisional decisions closed:**

- **Brief Q6** (`docs/03` Q6), plan-as-data within Temporal limits. Closes with a
  change: the continue-as-new threshold must be **events or bytes, whichever
  comes first**, because the payload ceiling binds before the event ceiling for
  large plans. Recommended 5,120 events. A plan size limit is now check 32 in
  `docs/12`.
- **The CEL implementation.** Cel.NET stays; all four checks pass. Four findings
  are recorded against `docs/09` 8.1 rather than the library.

**What Spike 1 proved that matters for Spike 2:** a worker crash mid-`agent.run`
resumes rather than restarts; a long infrastructure outage stalls rather than
fails; a re-plan mid-run leaves in-flight work untouched; no update is lost
across continue-as-new. The interpreter works. Spike 2 is the first time it meets
real providers.

---

## 2. Next: Spike 2

`docs/08` section 2.2 is the spec. Decided by the product owner, 2026-10-28
(review 13 section C).

| Item | Decision |
|---|---|
| Fixture repository | A **new repository in the `ai-feature-flow` organisation**: a small .NET API, a Postgres-backed integration tier (Testcontainers), a GitHub Actions workflow. It doubles as the open-source example |
| Agent runtimes | **Both GitHub Copilot CLI and Claude Code**, as two named instances of the agent runtime provider (review 07 Q3), selected by role mapping (`docs/09` 11.1) |
| Open models | **Copilot must support open models through bring-your-own-key model routers** (for example an OpenAI-compatible router endpoint). Show at least one role running on an open model through a router. Model and endpoint are configuration (organisation layer and credential references), never template content |
| Other runtimes | The agent runtime stays **pluggable**: nothing in the engine or library may assume Copilot or Claude Code. Record in `docs/16` which runtime capabilities (`hooks`, `session_resume`, usage reporting) each runtime actually provided |
| Credentials | **Ask the product owner for each credential when it is needed**, one at a time, saying what it is for and the narrowest scope that works. Never commit one; credentials are references to the secrets provider (floor F2). A local secrets file outside the repository is acceptable for the spike |
| Output | `docs/16-spike-2-results.md`, in the same form as `docs/15`: checks met or not, measurements, cost of the run from the record store, bugs found, and the numbers it replaces as it goes |
| Also in scope | The `insert_after` measurement (review 11 C2) and actual `outputs` sizes per kind (review 11 C1) |

**Read first:** `docs/08` 2.2 (the spec), `docs/15` (what Spike 1 established),
`docs/01` sections 3 and 4 (the interpreter and providers), `docs/04` and
`docs/05` (provider interfaces and contracts), `docs/09` (the plan schema).

---

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

**Watch for interference between child sessions.** In Spike 1 two agents shared a
worker and a sandbox, and one killed the other's worker mid-test, corrupting a
run. Give each child its own queue, port or directory where it touches shared
infrastructure, and say so explicitly in the task.

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
cycle has run thirteen times and works well.

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

The review board checks claims. Three times now a document asserted something its
own evidence did not support, and twice a measurement was invalid:

- Review 12 caught a **circular** threshold: 500 events was recommended because
  that was where continue-as-new triggered, and it triggered there because the
  test set the threshold to 500.
- Review 13 caught a measurement where **the steps never overlapped**: runs were
  started in a loop, the dev server returned "busy", so they started staggered.
  Per-run times of 0.24s for 20-second steps gave it away.

**Sanity-check the method before trusting the numbers.** For the slot
measurement, that meant running 20 steps against **2** slots first and confirming
that almost every activity waited about one step duration. If a measurement
method cannot reproduce an obvious case, it cannot measure a subtle one.

- Write a throwaway script to check examples against the validation table, then
  delete it.
- Parse every JSON schema block before committing.
- Say in the commit message what was verified and how.

### 3.5 Running the spike code

`spikes/01-interpreter/` is working code and is the reference for how the engine
behaves. To run it:

```powershell
cd spikes\01-interpreter
.\restart.ps1                      # stop spike processes, rebuild, restart
.\restart.ps1 -NoBuild             # restart without rebuilding
.\restart.ps1 -WorkerArgs '--fast' # shorten the timeouts that dominate tests
```

It needs a Temporal dev server on `localhost:7233`:

```powershell
temporal server start-dev --db-filename .spike\data\temporal.db --headless
```

**`--fast` matters.** The heartbeat timeout (30s), backoff cap (5 min) and
stalled threshold (5 min) dominate failure-path test time. With them, one outage
test takes about 12 minutes; with `--fast`, about 2. Pass it to **both** the
harness and the worker — they are separate processes, and setting it in one does
not reach the other.

**Stop dotnet processes before rebuilding.** The worker holds its DLLs and the
build fails with "file is locked by". `restart.ps1` does this, and stops only the
spike's own processes (matched by `--urls` and `--queue`), not every dotnet
process on the machine.

**The Temporal dev server does not reliably survive being stopped and restarted
with its state intact.** It lost a run during Spike 1. For an outage test, prefer
the block-access variant (a local proxy) over killing the server.

---

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
  proved the design handles it; Spike 2 must not regress it.
- **Privilege comes from the role, never from the step** (`docs/09` 11.0). An agent
  may not grant itself `write_access`, `tools` or `capabilities` through a patch.
- **A patch has three results, not two**: applied, pending human decision,
  rejected. An agent patch touching protected content becomes a pending human
  decision; only a floor violation is rejected outright.
- **A `run.child` branch routes nothing.** `replan_parent` is handled by the
  interpreter, not routed to an agent `replan` step, which would discard the
  child's patch. It counts as completed for the join, and the ticket is not
  automatically re-run.
- **The plan schema is versioned separately from the interpreter** (S14).
- **Carried state must be bounded by plan size, not steps executed.** The
  continue-as-new input is one payload with a 2 MB ceiling. About 500 bytes per
  plan step; a plan size limit is check 32 in `docs/12`.
- **Infrastructure failures never consume an attempt** (`docs/01` 3.7). A resume is
  free only with progress; after 3 progress-free resumes the next counts as a
  restart. A long outage stalls and alerts — it never fails on its own.

---

## 5. Open items carried into Spike 2

| Item | Where |
|---|---|
| The `outputs` cap of 16 entries and 256 bytes is a starting value | Spike 2 records actual sizes per kind (review 11 C1) |
| `insert_after` rewiring is argued, not measured | Spike 2: planner agent authors patches with both forms, record validation failure rates (review 11 C2) |
| The API must report platform status distinctly from run status | Deferred to the API work; recorded in `docs/08` 4.1 as an acceptance criterion |
| A pool-sizing rule for `docs/07` | The rule is arithmetic (slots at least equal to concurrent long steps, plus headroom). Review 13 asks for three repetitions per level; Spike 1 has two |
| Cel.NET's small maintainer base | Recorded as a risk with the fallback stated (`docs/09` 8.1) |
| `run.wait` | Reserved in `docs/06`, out of the version 1 language. Returns only if a worked example needs it |
