# Handover: Spike 3

Written 2026-10-30, at the close of Spike 2 and before starting Spike 3.

Replaces `HANDOVER-spike-2.md`, which is now stale: Spike 2 is closed. Delete
this file once Spike 3 is underway and the conventions below are in use.

---

## 1. Where things stand

**Spike 2 is closed.** Review 16, 2026-10-30: *"Spike 2 has answered its
question: yes, the design is feasible against real providers."*

| Deliverable | Document |
|---|---|
| Spike 1 results | `docs/15-spike-1-results.md` |
| Spike 2 results | `docs/16-spike-2-results.md` |
| Spike 1 code | `spikes/01-interpreter/` |
| Spike 2 code | `spikes/02-real-run/` |
| Fixture repository | `ai-feature-flow/feature-flow-example` |

**What Spike 2 proved.** A model authored the change (Copilot CLI on
`z-ai/glm-5.3-flash` through OpenRouter — `WidgetStore.IsEmpty()` and its
regression test), the plumbing around it is real, and a PR was merged through the
engine. The two halves were shown in different runs; review 16 accepted that as
enough for feasibility.

**What it cost to get there.** The first run claimed the exit criterion was met.
It was not: floor F1 was bypassed, and the agent's output was canned content
rather than model-authored work. Review 14 caught both. Read `docs/16` section 9
before assuming anything about Spike 2 — the first version of that document was
wrong, and the correction is the useful part.

---

## 2. Next: Spike 3

`docs/08` section 2.3 is the spec: one epic, three tickets, one repository,
`max_concurrency: 2`, with a **seeded cross-ticket defect** so the epic-level
review has something to find.

Review 16: *"Propose the Spike 3 plan in light of what Spikes 1 and 2 changed,
before building it."* So the first deliverable is a **plan**, not code.

`docs/16` section 11 lists what Spike 3 inherits. The short version:

1. The provider seam holds — real providers replaced the fake sandbox without
   touching the workflow.
2. A real model is available through OpenRouter on `copilot-cli`.
3. **R4 instability is real** — the same diff produces different findings on
   different passes. Epic-scope review will face this, and the seeded defect
   makes it measurable.
4. F1 and code host protection are settled as design requirements R2 and R3.
5. Claude Code is deferred, not dropped.

**Read first:** `docs/08` 2.3, `docs/16` (especially sections 9, 10 and 11),
`docs/15`, `docs/01` sections 3 and 4, `docs/04` and `docs/05`, `docs/09`.

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
infrastructure, and say so explicitly in the task. Spike 2 used queue
`ff-a3-queue` on port 7433 to avoid colliding with Spike 1's `spike-1` on 7233.

**Verify what child sessions claim.** In Spike 2 a child reported a
model-authored commit that did not exist anywhere in the fixture repository. The
report was internally consistent and the code was real, but the specific commit
hash was not recoverable. Check the decisive evidence yourself before repeating
it.

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

**It tolerates transient network failures.** A fetch that fails logs a warning and
keeps polling rather than exiting. This was fixed in Spike 2 after two blips
silently ended the loop. If output ever looks empty when a review is known to
exist, check this first.

**Known bug, fixed 2026-10-27:** `print_report` wrote review text to stdout, and
Python on Windows defaults to cp1252. Review text contains characters cp1252
cannot encode, so it raised `UnicodeEncodeError` — *after* the fetch had already
advanced `origin/<branch>`. `main()` now reconfigures stdout and stderr to UTF-8.

### 3.3 Review cycle

Work is pushed to `main` on `origin`, and the product owner replies with a review
file in `docs/reviews/`, numbered sequentially. Apply the review, then push. The
cycle has run sixteen times and works well.

**Keep design-repository work on `main`.** Spike 2 drifted onto a branch
(`ff/epic/spike-2`) because the F1 floor forbade merging into `main` without human
approval — which was correct for the *fixture* repository but unnecessary for the
design repository. Review 16 asked for it to be merged back and the branch
deleted. Do not repeat the drift.

**Commit trailer.** Every commit ends with:

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

**Commit messages.** On Windows, PowerShell mangles commit messages containing
double quotes. Write the message to a file and use `git commit -F <file>`, then
delete it. A `.gitignore` covers `*.tmp`, so this is safe.

**PowerShell has no heredoc.** `git commit -F -` with a here-string does not work.
Write the file first.

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
(R20); polling inside one activity fixed that but changed latency. When adding a
mitigation, ask what it costs.

**Grep for superseded forms before pushing.** After changing a name, a directive
form or an input, grep every document for the old form. Prose quoting the old form
to explain a change is fine; live usage is not.

### 3.4 Verify, do not assert

The review board checks claims. This is the convention that has caught the most:

- Review 12 caught a **circular** threshold: 500 events was recommended because
  that was where continue-as-new triggered, and it triggered there because the
  test set the threshold to 500.
- Review 13 caught a measurement where **the steps never overlapped**.
- Review 14 caught an exit criterion claimed on **canned content** substituted for
  the provider under test.
- Review 14 caught **two circular checks**: R4 passed because the verdict was a
  pure function of the diff hash, and the `insert_after` measurement generated its
  patches in code.

**A check that passes by construction proves nothing.** Before trusting a number,
ask what would make it come out differently. If the answer is "nothing", the check
is circular.

**Sanity-check the method before trusting the numbers.** For the slot
measurement, that meant running 20 steps against **2** slots first and confirming
that almost every activity waited about one step duration. If a measurement
method cannot reproduce an obvious case, it cannot measure a subtle one.

- Write a throwaway script to check examples against the validation table, then
  delete it.
- Parse every JSON schema block before committing.
- Say in the commit message what was verified and how.

**Do not substitute for the provider under test.** If a credential is missing,
stop and ask. A run that substitutes canned content for the provider it is meant
to prove must never be presented as meeting the exit criterion. Review 14's
process note: *"Stopping to ask for a credential is not a failure of autonomy; it
is the agreed working model."*

### 3.5 Review depth for spikes

Decided by the product owner, 2026-10-30 (review 16). Spikes prove that a design
concept is feasible before the design is formalised and the application is built.
Reviews of spikes therefore ask two questions only:

1. Does the evidence support the feasibility claim? (A check that passes by
   construction does not.)
2. Did the spike teach something that changes the design?

Production concerns found along the way are **recorded as design requirements**,
not fixed in spike code. Keep spike code as cheap as it can be while the evidence
stays honest.

### 3.6 Running the spike code

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

`spikes/02-real-run/` builds with `dotnet build` from `spikes/02-real-run/src`.
Its harnesses run against the real fixture repository and need credentials — see
section 5.

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
  and R19 are the same class, and it was the prototype's defining failure. Spikes
  1 and 2 proved the design handles it; Spike 3 must not regress it.
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
- **A floor must not depend on a file the governed party can edit.** Spike 2's F1
  check read the default branch from `feature-flow.yaml` on the ticket branch,
  which the agent can write to. It now reads it from the code host (review 15 A1).
  Ask this question of every floor.
- **A decision's actor kind comes from the API's authenticated identity, never
  from the request body** (`docs/01` 5.4). This is what makes F1 enforceable at
  all: a harness or policy actor cannot satisfy a human approval.

---

## 5. Credentials

**Ask the product owner for each credential when it is needed**, one at a time,
saying what it is for and the narrowest scope that works. Never commit one;
credentials are references to the secrets provider (floor F2). A local secrets
file outside the repository is acceptable for a spike.

What exists now:

| Credential | Where | Notes |
|---|---|---|
| GitHub App (id 5507595, installation 176108462) | `D:\Users\Stuart\keys\feature-flow-secrets.json` | Engine identity. 60-min installation tokens, one repository |
| OpenRouter key | `D:\Users\Stuart\keys\feature-flow-openrouter.txt` | Model router. `z-ai/glm-5.3-flash` |

Mint a GitHub token:

```powershell
python tools\gh_app_token.py --app-id 5507595 `
  --key $env:FF_GITHUB_APP_KEY --installation 176108462 --emit
```

**Pushing to the design repository.** `git push` prompts for credentials and
fails non-interactively. Set the remote URL with the token inline, push, then
restore it:

```powershell
git remote set-url origin "https://x-access-token:$tok@github.com/ai-feature-flow/feature-flow-poc.git"
git push origin main
git remote set-url origin "https://github.com/ai-feature-flow/feature-flow-poc.git"
```

**Model credentials never enter the agent's environment** (design requirement R1).
Spike 2 passed the key as an environment variable with all tools allowed. Version
1 uses a proxy or per-run keys. The spike's form is acceptable *for the spike*
because it is recorded as a known gap, not because it is right.

---

## 6. Open items carried into Spike 3

| Item | Where |
|---|---|
| **R4: review verdicts are unstable with a real model.** A fixed finding is re-raised; a new finding appears on unchanged input. How the findings ledger should treat a re-raised finding is a Spike 3 or design question | `docs/08` 1.2, 1.3; `docs/16` 8 |
| Code host protection should be configured by an identity the engine does not hold (parked review 15 A2) | `docs/08` 1.3 R3 |
| The App installation is org-wide; review 14 said tighten it to `feature-flow-example` | Not yet done — a UI action |
| B4: `docs/15` 6.3 slot measurement inconsistent, and the K4 decline-path re-run | Review 16: do only if Spike 3 needs them |
| `run.wait` | Reserved in `docs/06`, out of the version 1 language. Returns only if a worked example needs it |
| A pool-sizing rule for `docs/07` | The rule is arithmetic (slots at least equal to concurrent long steps, plus headroom). Review 13 asked for three repetitions per level; Spike 1 has two |
| Cel.NET's small maintainer base | Recorded as a risk with the fallback stated (`docs/09` 8.1) |
