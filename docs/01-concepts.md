# Concepts

Status: draft, 2026-10-31.

This document owns the domain model. Every other document uses these terms as
defined here and does not redefine them. Principles are cited by number (P1 to
P12, `docs/00-principles.md`); decisions by number (RW1 to RW18,
`docs/PLAN.md` section 3).

| Section | Concept |
|---|---|
| 1 | Runs and levels |
| 2 | Plans, templates and steps |
| 3 | Outcomes and routes |
| 4 | Attempts |
| 5 | Artifacts and evidence |
| 6 | Findings and the ledger |
| 7 | Decisions |
| 8 | Requirements and traceability |
| 9 | Repositories and branches |
| 10 | Changing a running run |

---

## 1. Runs and levels

A **run** is one execution of a workflow for one unit of work. It has an id, a
level, a plan, the repositories it works on, a requester, and a record of
everything that happened.

A **level** is a kind of unit of work named by the workflow: for example
`ticket`, `epic`, `project`. The engine attaches no meaning to the names. A run
may start **child runs**, usually at the level below; a child may start its
own. The runs form a **run tree** with one **root run**. A root run may cap the
number of descendants its tree creates (a work-item budget, from FF), within an
organisation ceiling.

### 1.1 Run status

| Status | Meaning | Set by |
|---|---|---|
| `running` | Started and not finished. A run waiting for a human is still `running` | Engine |
| `completed` | The run reached `{ end: completed }` | A route |
| `failed` | The run reached `{ end: failed }` | A route |
| `rejected` | The run reached `{ end: rejected }`: a human declined and the plan had nowhere else to go | A route |
| `partial` | The run reached `{ end: partial }`: some of its work landed and some did not | A route |
| `aborted` | A human stopped it | An override |
| `incomplete` | The run stopped with nothing ready, nothing in flight and no decision open, without reaching an `end` route. Never reported as success (P5) | Engine |

A parent sees a child's status as the outcome of the step that started it
(`docs/03`, child runs).

### 1.2 Run position

A run's **position** says what it is doing now. Because steps run in parallel,
a position is a set of entries, one per active step or wait.

| Entry | Meaning |
|---|---|
| `executing` | A step is running |
| `awaiting_decision` | A decision is open (section 7), with its kind |
| `awaiting_child` | A child run is in progress |
| `awaiting_condition` | A wait for an external state (a pull request merged, a tracker status) |
| `stalled` | A provider has been unreachable for longer than the organisation's threshold. The run alerts and waits; it never fails on its own (P5) |

Position, not status, tells a human what they can do about a run. The idea is
from FF, extended to parallel steps.

---

## 2. Plans, templates and steps

A **template** is a workflow definition with unbound parameters, kept in the
library. A **plan** is the same document with its parameters bound. Binding
happens at run start and produces **plan revision 1**. A patch (section 10)
produces the next revision. Revisions are immutable and all are kept.

A plan is an ordered list of **steps**, plus the run's parameters, defaults and
artifact types. Every step has an id, unique in the run and never reused for a
different step, even after the step is removed (from FF).

### 2.1 Three step forms

| Form | Who does the work | Examples |
|---|---|---|
| **Agent step** | An LLM agent, in a sandbox, with a role and a task | Plan, implement, review, fix, triage, resolve a conflict |
| **Decision** | A human, or a policy that may resolve it automatically | Approve a specification, choose how to recover a partial delivery |
| **Engine action** | The engine itself, deterministically. No LLM | Run tests, wait for CI, merge, start a child run, validate artifacts, record findings |

Two plan constructs are not steps but organise them: **parallel** (run several
branches at once and join them) and **fragments** (a reusable group of steps
from the library, expanded when the template loads). Both are defined in
`docs/03`.

### 2.2 Roles

An agent step names a **role**: library content that says what the agent may
do. The role sets the agent's write access (none, its own branch, or the whole
repository), its tools and toolsets, and which runtime capabilities it needs. A
step may tighten its role's write access but never widen it, and may not change
its tools (P8). Which runtime and model a role runs on is organisation
configuration.

### 2.3 Protected steps

A step marked **protected** cannot be removed, changed or made unreachable by a
patch an agent proposes. Such a patch becomes a decision for a human (section
7). A human with the right role may change or bypass a protected step; the
override is audited (P3, RW8).

---

## 3. Outcomes and routes

Every execution of a step ends with exactly one **outcome**, a name. The plan's
**routes** say where the run goes next for each outcome.

### 3.1 Declared outcomes

Agent steps and decisions **declare** the outcome names they may produce
(RW3). The names are the workflow's: `accepted` and `rejected` for a reviewer,
`approved`, `rejected` and `needs_research` for a gate. The engine refuses an
outcome the step did not declare.

Each declared outcome may carry a **class**:

| Class | Meaning | Used for |
|---|---|---|
| `success` | The step did what it was for | The default next step (section 3.3); reporting |
| `failure` | The step's work was wrong or rejected | Plan-level route defaults; reporting |
| `blocked` | Something outside the step must change first | Plan-level route defaults; reporting |

An outcome without a class has no default route and must be routed explicitly.

Engine actions have **fixed** outcome sets, each with a fixed class, defined in
their contracts (`docs/04`).

### 3.2 Outcomes the engine produces

| Outcome | When | Class |
|---|---|---|
| `attempts_exhausted` | The step is about to start but its attempt allowance is spent (section 4) | `failure` |

No other outcome is added by the engine. In particular, an agent step that
produces malformed output, or proposes an invalid patch, does not get a special
outcome: the engine returns the errors to the agent for correction (section
5.4), and if the output is still invalid the attempt fails and the step is
retried within its allowance.

### 3.3 Routes

A route maps an outcome to one of:

| Target | Effect |
|---|---|
| A step id | Run that step next. If it has run before, it runs again: its current outcome, outputs and artifacts are cleared, and its earlier results stay readable as its previous execution |
| `{ end: <status> }` | End the run with that status (section 1.1) |
| `{ escalate: true }` | Raise an intervention (section 7) and wait for a human to say what happens next |

A route may be **guarded**: an ordered list of conditions over run state, each
with a target, ending in an unguarded fallback. A route may carry
**`clean_up`**: when it goes back to redo earlier work, reset the working
branch to where that work started, so the redo does not inherit the failed
attempt's commits (from FF, RW16).

Routes do not have to be written out for every outcome (P10):

| Default | Rule |
|---|---|
| Next in list | An outcome of class `success` with no route goes to the next step in the list (RW13) |
| Plan-level defaults | A plan may route outcomes by name or by class for every step, for example `failure: { escalate: true }` |
| Engine default | `attempts_exhausted` with no other route escalates |

After defaults are applied, every outcome a step can produce must have a route,
or the plan fails validation. The resolved routes are what validation checks
and what a human sees (P11).

---

## 4. Attempts

An **attempt** is one execution of a step. **Every entry into a step counts
against that step's allowance**, whatever route led there: a first run, a loop
back, a retry. This is one rule for every step (FF had two, depending on which
features a workflow used).

| Rule | Detail |
|---|---|
| Allowance | Each step has an attempt allowance: an engine default per form, overridable by the workflow, a child workflow and the step, within an organisation ceiling. The run also has a ceiling on all attempts |
| Checked on every execution | Before a step starts, its allowance and the run's ceiling are checked. If spent, the step does not run and its outcome is `attempts_exhausted` |
| Infrastructure failures are free | An attempt cut short because a provider or the platform failed is retried and does not count (`docs/06`, error classification) |
| Resumes are free only with progress | An agent step resumed after an interruption does not count, provided the agent made progress since the last resume: a new commit, or a longer session. After 3 resumes in a row without progress, the next one counts |
| Correction turns are not attempts | Returning output errors to a running agent so it can fix them (section 5.4) happens inside one attempt |
| Granting more | A human with the right role may grant extra attempts, up to the organisation's hard ceiling (P3) |

Everything an attempt used is recorded by content hash: the rendered task, the
library content, the repository manifest, the consumed artifacts and the commit
(P7).

---

## 5. Artifacts and evidence

### 5.1 Artifacts

An **artifact** is an immutable, content-addressed record produced by a step: a
plan document, a specification, a list of tickets, a test result, a findings
report. Artifacts are what later steps rely on.

| Property | Meaning |
|---|---|
| Name | A logical name, unique in the run: `delivery_plan`, `implementation_claims` |
| Type | A library-declared artifact type: a schema, and the validators that check it |
| Revision | Each time a step produces the same name again, a new revision is created. Earlier revisions are kept |
| Producer | The step and attempt that produced it |
| Kind | A **document** (bytes in the artifact store), or a **reference** to something outside: a commit, a branch, a pull request, a tracker issue, a child run |

A step declares what it **produces**, per outcome, and what it **consumes**. A
consumed artifact is pinned at the revision current when the attempt starts. It
may come from this run, the parent run, the root run, or a named child (from
FF).

An **artifact set** is a named group of artifacts treated as one subject, so a
validator, reviewer or human can judge them together.

### 5.2 Evidence

**Evidence** is an artifact the engine produced from its own observation (P6).
Artifact types are marked as evidence in their definition, and only engine
actions may produce them. A workflow in which an agent step declares that it
produces an evidence type fails validation.

| Evidence type | Produced by |
|---|---|
| Test results | The engine running a repository's declared test tiers, or reading them from CI |
| Static analysis results | The engine running the repository's analyser, or reading it |
| Check results | The engine reading a pull request's checks |
| Merge records | The engine merging, or observing a human's merge on the code host |
| Validation reports, trace reports | The engine running validators |

An agent may produce **claims** about evidence, for example "acceptance
criterion AC-2 is proved by test `OrderTests.RejectsNegativeQuantity`". A claim
is an ordinary artifact; a validator checks it against the evidence (section
8).

### 5.3 Verdicts and ratification

| Act | Meaning |
|---|---|
| **Verdict** | An agent step or a decision records, per outcome, that it accepted or rejected specific consumed artifacts, at their exact revisions |
| **Ratification** | A decision records, per outcome, that a human approved specific artifacts at their exact revisions. Child runs inherit the ratification as their authority. A later revision is not ratified until a new decision ratifies it |

Both apply only to the revisions named. Neither re-runs validation. The idea is
from FF; the first Feature Flow design did not have it.

### 5.4 Validation of produced artifacts

When an agent step finishes, the engine checks every artifact it produced
against its type's schema and validators. Errors are named by field and rule,
and go back to the agent **in the same session** for a bounded number of
correction turns. Only if the artifact still fails does the attempt fail. The
same applies to a patch an agent proposes (section 10).

---

## 6. Findings and the ledger

A **finding** is a problem raised by a review: a possible bug, a breach of
convention, a gap against the specification.

| Property | Meaning |
|---|---|
| Fingerprint | Rule or check id, repository, file path, enclosing symbol, and a hash of the normalised code snippet. **Never line numbers**, which move |
| Severity, blocking | As the reviewer reported them |
| Basis | `demonstrated` (the reviewer wrote a failing test or other proof) or `reasoned` |
| Source | Which review step or external reviewer raised it, and which model |
| State | `open`, `fixed`, `declined` (with a reason), `escalated` |

The **findings ledger** is the run's record of every finding and its history.
A review pass adds to the ledger; it does not replace it. When a new finding
matches an existing one by fingerprint, the engine records it as a **re-raise**
of that finding rather than a new one, and records whether the code at the
fingerprint has changed since the finding last changed state. A parent run's
ledger view includes its children's ledgers, so a parent-level review is
matched against what the children already found and fixed.

| The engine provides (mechanism) | The workflow decides (policy, with library defaults, RW4) |
|---|---|
| Recording findings from a findings artifact or an external reviewer | Which findings block a merge (the merge bar) |
| Matching by fingerprint; recording re-raises and code changes | Whether a re-raise of a fixed or declined finding reopens it |
| State changes, each with actor, reason and time | Who or what triages: an agent, a human, or both |
| Queries for routes: counts by state, severity, basis | Whether a fix must add a regression test |
| Posting a decline reason on the pull request | When repeated re-raises escalate |

The standard library's defaults reproduce the positions the first design
evidenced: a fixed or declined finding stays so while its code is unchanged;
new findings on unchanged code are kept, not voted away; a fix needs a
regression test unless the triage records why not (`archive/docs/17` section
6.5).

---

## 7. Decisions

A **decision** is a question put to a human, which policy may sometimes answer
instead. It is the one mechanism for every kind of human wait (P4).

| Property | Meaning |
|---|---|
| Decision type | A namespaced name, declared in the library: `ticket.spec_approval`, `delivery.partial_recovery`. Organisation policy says who may answer each type, by pattern (`docs/05`) |
| Prompt and context | What the human is asked, and the artifacts they should look at |
| Choices | The answers allowed. For a decision step these are its declared outcomes |
| Auto-resolution | The answer policy gives without a human, where the template asks for it and the organisation allows it |
| Answer | The choice, the authenticated actor, a reason, and the time |

A decision is a durable wait. It has no timeout; reminders are sent on a timer.
Other steps keep running while it is open.

### 7.1 Kinds of decision

| Kind | Raised by | Answered with |
|---|---|---|
| **Gate** | A decision step in the plan. It may open an **interactive session**: a workspace where the person works in their own agent tool, runs the later steps' checks on demand, and submits files that become artifacts (RW18) | One of the step's declared outcomes |
| **Intervention** | The engine, when a route escalates or something unexpected stops a step | A recovery action (section 7.2) |
| **Question** | An agent, through the `ask_human` tool, during its step (RW14) | Free text, or one of the options the agent offered. The agent waits with its session intact |
| **Patch approval** | An agent's patch that touches protected content, or uses a role outside the organisation's allowed list | Apply or reject |
| **Action request** | A step that needs a human to act outside the engine, such as merging into the default branch on the code host | Observed: the engine detects that the action happened. A human may also decline |
| **Breakpoint** | A human, asking the run to pause before a named step | Continue, or any recovery action |

### 7.2 Recovery actions

The answers to an intervention or a breakpoint:

| Action | Effect |
|---|---|
| Retry | Run the stopped step again |
| Go to | Run a named step next, optionally with `clean_up` |
| Re-plan | Run a named planning step next, which may propose a patch |
| Grant attempts | Add attempts to the step's allowance, then retry |
| End | End the run with a chosen status |
| Continue | Breakpoints only: start the step that was paused before |

Each is an override (section 10) and is bounded by organisation roles.

---

## 8. Requirements and traceability

The brief requires traceability from requirements to acceptance criteria to
tests. FF implemented it; this section defines it for Feature Flow. The engine
supplies the mechanism; the shapes of the documents are library content (RW7).

| Concept | Meaning |
|---|---|
| **Requirement** | A stable, observable behaviour that must hold. At the top, written with a person; lower levels have requirements that support those above |
| **Acceptance criterion** | An observable condition a unit of work must meet, supporting one or more requirements |
| **`supports`** | The upward link from every item to the items it serves. An item that supports nothing gives a reason (enabling work) |
| **Owner** | In a plan a parent writes for its children, the child (or the parent itself) that must prove each item. A child copies the items it owns |
| **Verification method** | The planned source of evidence for a criterion: a test at a named repository and test tier |
| **Evidence claim** | The agent's mapping from a verification method to exact test identities |
| **Trace** | Every requirement followed through its owners and supporting items to claims, and each claim checked against the engine's evidence (section 5.2), across child runs |

What the engine checks, and what it does not:

- Checked (by the standard trace validator): every requirement has supporting
  items; every item has an owner that ran; every criterion's claims name tests
  that the engine observed passing at a commit the merged work contains.
- Not checked: whether the supporting items are **enough**. That is a judgement
  for a review step.

A gap in the trace is a validator failure, which a workflow normally routes to
stop the run before anything merges.

---

## 9. Repositories and branches

| Concept | Meaning |
|---|---|
| Run repositories | The repositories a run works on. A step works on one, or fans out over several (`docs/03`) |
| Repository manifest | `feature-flow.yaml` at the repository root: how to set up, test, analyse and run the repository. Read fresh from the branch at every step, with its hash recorded (RW11). Its `library` section is read from the default branch only, so an agent cannot change what it is given (`docs/07` section 4) |
| Work branch | Each run has its own branch in each repository it changes, taken from a **parent branch**: the parent run's branch, or a base the workflow names (usually the default branch) |
| Baseline | The commit a run started from in each repository, recorded when the run first touches it. Baseline test and analysis results let later failures be attributed to the run's own work (from FF) |
| Hidden work-in-progress reference | Where the engine pushes an agent's commits while its step runs. It is not a branch, so it triggers no CI and exposes no half-finished work. The work branch moves only when the step succeeds |
| Merge | Work merges back into its parent branch. The workflow catches the work up with the parent and re-tests if that changed anything; the engine refuses to merge a commit that has no passing test or check evidence, where the merge step requires it (`docs/04` section 7.3) |
| Default branch | The repository's main branch, read from the code host. Whether the engine may ever merge into it is a safety floor (`docs/05`) |

Agents never hold a credential that can push or merge. They commit in their
sandbox; the engine pushes (P2).

---

## 10. Changing a running run

Three ways, each with one path (P4).

| Change | What it changes | Proposed by | Applied by | Owned by |
|---|---|---|---|---|
| **Patch** | The plan's structure: steps, routes, inputs, limits | An agent step with planning authority, a child run, the engine's deterministic expansion of a list, or a human | One function, which validates it against the plan's rules and protected steps. An applied patch names the step the run continues at | `docs/03` |
| **Library override** | The content future steps resolve: a task, a prompt, a schema | A human, or an agent (as a proposal) | The engine, as an entry in the run's library lock | `docs/08` |
| **Human override** | Anything else about the run: answer a decision, skip or bypass a step, grant attempts, abort | A human, through the API | The engine, after authorisation by role | `docs/05` |

Every change is recorded with its actor, reason and the difference it made. An
agent's proposal that touches protected content becomes a patch approval
decision (section 7.1). No change of any kind relaxes a safety floor.

A child run's request to change its parent's plan is a patch it sends while it
keeps running or waits. The parent applies it like any other; the child's
branch of the parent's join is counted only when the child ends (RW6).

---

## 11. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entries 1 to 7.
