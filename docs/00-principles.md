# Principles

Status: draft for review, 2026-10-31.

This document states the principles every other design document applies, and
the terms they use. When a design question arises, it is settled here first.
Each principle says what it means, why it exists, and what follows from it.

## 1. What Feature Flow is

Feature Flow is an orchestrator for agent-assisted software delivery. A
developer starts a run for a ticket, an epic, a project or any other unit of
work. The engine drives the run through a workflow of steps (planning,
specification, implementation, review, testing, merging) across one or more
repositories. LLM agents do most of the work, inside sandboxes. The engine
verifies their work independently and stops for a human only at an explicit
decision point or an unexpected condition.

It complements an organisation's existing tracker, code host, CI and human
review; it does not replace them. It runs on Temporal, a durable execution
platform, so a run survives crashes, deploys and outages. Adopters range from
one developer on a laptop to a large organisation; the same code runs for all of
them, and only configuration and plug-ins differ.

It succeeds two prototypes:

| Prototype | What it proved | What it lacked |
|---|---|---|
| Feature Factory (FF): a local TypeScript process with Git-backed state | The delivery model: workflow templates in a library, free outcomes and routing, re-planning, requirement traceability, engine-verified evidence. It delivered a real feature across 3 repositories | Durability, server deployment, pluggable systems, review at every level, a single policy model |
| Feature Flow first design (`archive/docs/`) and its three spikes | The engine: a Temporal interpreter for plans held as data, resumable agent steps, error classification, lock-free merging | A small workflow surface: policy had moved into the engine, and several FF facilities were missing (`archive/review-vs-feature-factory.md`) |

## 2. The principles

### P1. Guardrails in the engine, policy in the workflow

The engine provides **mechanisms** (ways to do things) and **guardrails**
(limits no workflow may cross). The workflow provides **policy**: what is done,
in what order, by whom, and to what standard.

- The workflow decides, among other things: the levels of work and their names;
  which steps run; what each review checks; what counts as ready to merge;
  whether a fix needs a regression test; when and how work reaches the default
  branch; how many times something is retried.
- The standard library supplies sensible defaults for all of these. A workflow
  that wants different behaviour changes its template, not the engine.

Why: FF moved process out of its engine into the library and became easier to
adapt (`../feature-factory/docs/design/engine-flexibility.md`, "restrictions and
process belong in the library"). The first Feature Flow design moved some back
(the regression-test rule, the merge bar, finding escalation) and lost
flexibility for no gain in safety.

### P2. The guardrail test

> A rule belongs in the engine only if it is needed for the engine to be safe,
> or for the run's record to be trustworthy, **whatever the workflow says**.

| Passes the test (engine guardrail) | Fails the test (workflow policy, with a library default) |
|---|---|
| Agents never hold a credential that can push or merge | Every fix adds a regression test |
| Model credentials never enter an agent's environment | A finding fixed twice escalates |
| Secrets are referenced, never inlined | The merge bar: no unresolved blocking finding |
| Attempt ceilings, checked on every execution of a step | Which review angles run at which level |
| Whether the engine may merge into a default branch, capped by the organisation | Whether a merge is a squash |
| Evidence the engine produces cannot be produced by an agent | Whether a merge conflict goes to an agent or a human |
| Every decision records an authenticated actor | Whether a reviewer must read every changed file |
| A run that did not reach an end route never reports success | How a delivery is ordered across repositories |

The authoritative list of guardrails is owned by `05-guardrails-and-policy.md`.

### P3. A human may override anything, within organisation limits

An authorised human can change a running run in any way: amend a step, skip or
bypass a step (including a protected one), change routing, override library
content, grant extra attempts, answer for an agent, or abort.

| Limit | Meaning |
|---|---|
| Safety floors | No override relaxes a safety floor (P2's left column). The floors are the only absolute rules |
| Organisation roles | Who may perform which kind of override comes from organisation policy, tied to identity-provider roles |
| Audit | Every override records the authenticated actor, the reason and the change. The actor comes from authentication, never from the request |

Agents are not humans. An agent may **propose** a change; if it touches
protected content it becomes a pending human decision, never silently applied
and never silently refused.

Why: FF let a person amend a run's workflow freely (`ff amend-step` was not
bound by `protected`), and a supervising agent had to keep runs moving with
scripts. Mid-run steering is a first-class, audited engine capability (brief,
section 3).

### P4. One mechanism per concern, the same at every level

Each concern has one mechanism, and that mechanism works the same way at every
level of work.

| Concern | One mechanism |
|---|---|
| Changing a plan during a run | A patch, applied by one function, whether it comes from an agent, a child run or a human |
| Waiting for a human | A decision: a type, a prompt, a set of choices. Gates, interventions, agent questions, merge requests and patch approvals are all decisions |
| Running an agent | The agent step. Reviewers, planners, fixers and implementers are agent steps with different roles |
| Levels of work | A run starts child runs with the same interpreter and the same plan language. Levels are named by the workflow and nest to any depth |
| Limits | Every limit has an engine default, overridable by the workflow, a child workflow and the step, within organisation ceilings |

Why: the first design had six ways to wait for a human and two ways to run an
agent. FF had two rule sets for attempt counting, depending on which features a
workflow used (`../feature-factory/issues.md`).

### P5. An interruption becomes a continuation, never a wrong outcome

A crash, deploy, network loss or platform outage pauses a run. It never ends
it, fails it, or reports it as successful.

- Infrastructure failures retry and never use up a step's attempts.
- A long agent step resumes where it was, or from its last pushed commit.
- A run that stops without reaching an end route has status `incomplete`.

Why: interruptions were FF's main operational pain (the brief, section 3: the
orchestrator hung, stalled after every step, and stopped when a laptop slept).

### P6. Evidence the engine observed outranks what an agent asserts

Later steps rely on what the engine verified itself: test results it parsed,
checks it read from CI, merges it saw on the code host. An agent's claim is
checked against that evidence, never taken as proof.

- Some artifact types (test results, analysis results, trace reports) can be
  produced only by engine actions.
- Events (hooks, webhooks, chat callbacks) only wake the engine; a read of the
  remote system confirms what happened.

Why: FF's verification model rested on this, and the first Feature Flow design
did not carry it over. In Spike 2 an agent's claimed output turned out to be
canned content (`archive/docs/16`, introduction and section 9).

### P7. Everything a step used is recorded

Every input that can change what a step does is recorded by content hash: the
library content (pinned for the run at start), the repository manifest (read
fresh from the branch at each step), the consumed artifacts, the rendered
prompt, the commit.

Why: an audit must answer "what exactly did the agent see". FF froze attempt
inputs; the first design pinned library content in a lock. Repository facts are
read per step rather than pinned, because FF went stale by copying them at run
start.

### P8. Privilege comes from role and identity, never from the step or the request

- What an agent may do (write access, tools) comes from its role. A step may
  tighten it, never widen it.
- What a human may do comes from their authenticated identity's roles.
- A safety setting is never read from a file the governed party can edit.

Why: otherwise an agent could grant itself access by editing its own plan, and
Spike 2's merge check read the default branch from a file the agent could
change (`archive/docs/16`).

### P9. Every external system is a provider

Trackers, code hosts, CI systems, sandboxes, agent runtimes, reviewers, stores,
secrets and notifications sit behind provider interfaces. Each provider declares
its capabilities, classifies its errors and passes a shipped contract test
suite. A run checks every capability it needs when it starts, so it fails in
its first minute rather than at step 40.

Why: the brief requires no assumption about a particular tracker, code host, CI
or cloud, and an organisation must extend the system without changing its core.

### P10. Short by default, explicit where it matters

A simple workflow should be short to write and easy for a planner agent to
change. Defaults cover the common case (the next step, escalation when attempts
run out); an author states only what differs. Validation still sees the fully
resolved plan, so nothing is implicit at run time.

Why: in the first design a single-ticket workflow was about 270 lines, and
`attempts_exhausted` was routed by hand 15 times.

### P11. Fail early and loudly

Anything that can change routing or execution is validated before it can take
effect, and a failure blocks with a message naming the field and the rule.
Warnings are cosmetic only. An unexpected condition stops the run with an
intervention; the engine never guesses.

Why: in FF, warnings about workflow logic were easy to ignore, and a guessed
success (an unrouted merge failure) once looked like a completed run.

### P12. One fact, one place

Each rule and each definition has one owning section in these documents. Any
other document gives one sentence naming the rule and its effect, and a
reference. It does not restate the rule.

Why: the first design restated the same rules in up to 13 documents, and the
copies came to disagree.

## 3. Who decides what

| | Engine | Workflow (library and templates) | Organisation | Human at run time |
|---|---|---|---|---|
| Provides | Mechanisms, safety floors, the record | Process and its defaults | Caps, roles, identities, provider choice | Decisions and overrides |
| Examples | Agent step, decisions, patches, merges, ledgers, validators, attempt counting | Steps, routes, reviews, merge bar, levels, delivery order | Who may approve, ceilings, whether the engine may ever merge into a default branch | Approve, answer, amend, bypass, grant attempts, abort |
| Bounded by | — | Engine floors and organisation caps | Engine floors | Engine floors and organisation roles |

Precedence, most specific first: a human override at run time, then the step,
then the workflow, then the organisation's defaults, then the engine's
defaults. Organisation caps and engine floors bound every one of them.

## 4. Glossary

| Term | Meaning |
|---|---|
| Run | One execution of a workflow for one unit of work |
| Level | A kind of unit of work, named by the workflow: for example ticket, epic, project. A run at one level may start child runs at the level below |
| Plan | The data the interpreter executes: the workflow's steps and routes, with parameters bound. Changed only by patches |
| Template | A plan with unbound parameters, kept in the library |
| Step | One node of a plan: an agent step, a human decision or an engine action |
| Outcome | The single named result of one execution of a step. Routes select on it |
| Route | Maps an outcome to the next step, or to a directive such as `end` or `escalate` |
| Artifact | An immutable, content-addressed record produced by a step |
| Evidence | An artifact the engine produced from its own observation, which no agent can produce |
| Finding | A problem raised by a review, recorded in the run's findings ledger |
| Decision | A question put to a human (or to policy), with choices, answered by an authenticated actor |
| Intervention | A decision the engine raises itself because something unexpected stopped a step |
| Patch | A change to a running plan |
| Override | A change a human makes to a running run, through the API, audited |
| Guardrail, safety floor | A rule the engine enforces that no workflow or override may relax |
| Provider | A plug-in implementing one external system behind an engine interface |
| Library | Versioned workflow content: templates, tasks, skills, roles, artifact types, validators, prompts |

## 5. Open questions

None open.

### Answered

Product owner, 2026-10-31.

| # | Question | Answer |
|---|---|---|
| 1 | P3 lets an authorised human bypass a protected step. Should some steps be marked so that even a human bypass needs a second approver (the old "mandatory" idea from FF)? | No, for version 1. Organisation roles already limit who may bypass, and the audit shows it (decision RW10) |
| 2 | P7 reads repository facts fresh at each step. Should a workflow be able to pin them for a run instead? | No. Pinning brings back the staleness FF suffered (decision RW11) |
