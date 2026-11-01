# SP3: the agent reporting contract over MCP on two runtimes

Status: plan and checks pre-registered 2026-11-01 (commit `d65a7e4`), before
any run; T3a added by amendment (commit `64efbf1`) before it ran. Run the same
day. **The claim holds on both runtimes**: every check passed 5 of 5, once the
harness prepared the output folder for the file fallback. Copilot CLI also
fired `PreToolUse` hooks on every tool call with a first-party model, which
Spike 2 had not seen.

## 1. Claim under test

An agent step ends by calling the engine's `report` tool: one declared outcome,
a short summary, outputs, and the paths of the artifacts it produced
(`docs/04` section 3.3). The engine validates the report. If it is invalid, the
errors go back to the agent as **correction turns** (at most 2 by default),
inside the same attempt; the last valid call counts. A runtime that cannot call
MCP tools writes the same report to a file. An agent that ends without
reporting has failed its attempt.

The claim is that **Claude Code and GitHub Copilot CLI use these tools
reliably**: they report when told to, report validly, correct a rejected report,
stop when the engine says the attempt has failed, use the file fallback when the
tool is absent, and wait on `ask_human`. Risk R23 in `docs/10` section 2.

## 2. What is built

`spikes/06-reporting`. No Temporal: the claim is about the interface between
runtime and tools, so the engine side is one stdio MCP server.

| Part | Behaviour |
|---|---|
| Engine tools (Python, stdio MCP server) | Serves `report` and `ask_human`. Validates each report against the contract below and logs every call with its verdict. Can inject a rejection on the first call (T2), or reject every call (control N2). After the second correction turn it answers "attempt failed: stop". `ask_human` waits for an answer file the harness writes |
| Contract | Outcomes `implemented` (class success) and `blocked` (class failure). `summary` non-empty, at most 2 KB. `outputs.files_changed` an integer; `outputs.risk` one of `low`, `medium`, `high`. `artifacts` lists `notes.md`, which must exist and be non-empty |
| Prompt | A reporting preamble stating the contract, then a small task: write `notes.md` with three bullet points on what a function `add(a, b)` should do |
| Harness (Python) | Runs each trial in a fresh sandbox directory, captures the runtime's output and the tool log, and evaluates the checks |

| Runtime | Model | Isolation from the user's own configuration |
|---|---|---|
| Claude Code 2.1.316 | `haiku` (the spikes' convention) | `--setting-sources project`, `--strict-mcp-config` |
| Copilot CLI 1.0.105 | The CLI's default model under the product owner's Copilot subscription, recorded in the results | Built-in MCP servers and custom instructions disabled; user hooks and settings excluded where the CLI allows, as recorded in the results |

## 3. Tasks and pre-registered checks

Five trials per task per runtime.

| # | Task | Engine behaviour |
|---|---|---|
| T1 | Do the task and report | Validates normally |
| T2 | As T1 | Rejects the first report with a rule stated only in the error: the summary must begin with `FF-142: ` |
| T3 | As T1, with no MCP server configured. The preamble says: if the `report` tool is not available, write the same report as JSON to `out/report.json` | None |
| T4 | Ask a question with `ask_human`, write the answer into `notes.md`, then report with `outputs.answer` set to it | Answers 30 s after the question opens, with a nonce the agent cannot guess |

**Exercised:** in T2, the engine rejected the first report in every trial (it
always does; this confirms the injection ran). In T4, the tool call was open
for at least 30 s.

**Claim checks**, per runtime. A check passes at 5 of 5 trials. 4 of 5 is
reported as partial and investigated; fewer is a fail.

| # | Check | Pass in a trial |
|---|---|---|
| C1 | Reports | T1: the engine logged at least one `report` call, and the last call was valid |
| C2 | Corrects | T2: after the injected rejection, a second call was valid and began with `FF-142: `, within the 2 correction turns; no further call after acceptance |
| C3 | Falls back | T3: `out/report.json` exists and is valid against the contract |
| C4 | Waits and uses the answer | T4: one `ask_human` call; `notes.md` and `outputs.answer` contain the nonce; the last report valid |
| C5 | Ends cleanly | In every trial of T1 to T4, the runtime exited 0 within 5 minutes |

**Negative controls.** Each must make the named check fail, on each runtime,
in at least one trial:

| # | Change | Must fail |
|---|---|---|
| N1 | T1 with no MCP server and no fallback instruction | C1: no report reaches the engine, so the checker is not satisfied by anything else the agent does |
| N2 | T2, but the engine rejects every call with an impossible rule | C2. Also records whether the agent stops after "attempt failed: stop" (at most 3 report calls) |

**Exploratory, not pass or fail:**

| # | Question |
|---|---|
| X1 | Does Copilot CLI fire a `PreToolUse` hook with a first-party model? Spike 2 saw it not fire with a third-party model (`archive/docs/16` section 4). A hook that logs each call is configured in every Copilot trial |

**Amendment, made after the first T3 runs and before re-running them.** In all
five Copilot T3 trials, Copilot's file tool refused to write `out/report.json`
because `out/` did not exist, and the shell it would have used to create the
folder is denied in these trials. Claude Code's file tool creates folders, so
it was unaffected. The engine prepares a step's output folder, so the harness
now creates `out/` before the agent starts (T3a). T3a is run five times on each
runtime under the same C3; the first T3 results are kept as run.

## 4. Out of scope

`propose_patch`, `inspect_plan`, `checkpoint` and `run_check`; worker restarts
(SP2); real repositories and real work. The measure is tool use, not the
quality of the work.

## 5. Results

Raw evidence per trial in `spikes/06-reporting/runs/<runtime>-<task>-<trial>/`:
`summary.json` (checks), `tools.jsonl` (every engine tool call and verdict),
`stdout.jsonl` (the runtime's own stream), `hook.jsonl` (Copilot only) and the
sandbox. Run with `python3 run.py <claude|copilot> <task> <trial> --work <dir>`
in `spikes/06-reporting`. Evidence scanned for credentials and identities:
none.

| Runtime | Model | Tool approvals |
|---|---|---|
| Claude Code 2.1.316 | `claude-haiku-5-5` | `Write` and the engine's tools |
| Copilot CLI 1.0.105 | `claude-sonnet-5.5`, the CLI's default on the product owner's subscription | File writes and the engine's tools; shell denied. Its own `ask_user` tool disabled. A separate `COPILOT_HOME`, so no user hooks, agents or plugins loaded |

The runtimes ran different models, so a difference between them may come from
the model, not the runtime.

### 5.1 Claim checks

| Check | Claude Code | Copilot CLI |
|---|---|---|
| C1 reports (T1) | 5 of 5 | 5 of 5 |
| C2 corrects (T2): rejected once, accepted on the second call with `FF-142: `, nothing after | 5 of 5 | 5 of 5 |
| C3 falls back (T3, folder absent) | 5 of 5 | **0 of 5**: see below |
| C3 falls back (T3a, folder prepared) | 5 of 5 | 5 of 5 |
| C4 waits 30 s and uses the answer (T4) | 5 of 5 | 5 of 5 |
| C5 exits 0 within 5 minutes (T1 to T4) | 25 of 25 | 25 of 25 |

The exercise checks held: the first T2 report was rejected in all 10 trials,
and every T4 question stayed open for at least 30 s.

**T3, first runs.** In all five Copilot trials the agent tried the fallback.
Its file tool refused `out/report.json` ("Parent directory does not exist"),
the shell it then tried (`mkdir -p out`) was denied, and it ended by saying it
could not report, exit 0. This tested the harness, not the claim: the engine
prepares the output folder. The amendment in section 3 added T3a, which passed.
It also shows the runtimes' file tools differ (Claude Code's creates folders;
Copilot's does not), so the engine must not rely on either.

### 5.2 Negative controls

| Control | Target | Claude Code | Copilot CLI |
|---|---|---|---|
| N1 no tool, no fallback instruction | C1 | Fail, 2 of 2: no report reached the engine | Fail, 2 of 2 |
| N2 every report rejected | C2 | Fail, 2 of 2. Stopped after 2 rejections, saying it could not get the report accepted | Fail, 2 of 2. One trial made the third call, got "attempt failed: stop" and stopped; one stopped after 2 rejections |

No agent called `report` after "attempt failed", and none made more than 3
calls. An agent that gives up early ends without a valid report, which the
engine already treats as a failed attempt (`docs/04` section 3.3).

### 5.3 X1: hooks on Copilot CLI

`PreToolUse` fired on every Copilot tool call in all 29 Copilot trials: the
hook's call count equals the number of tool starts in Copilot's own output in
every trial. It fired for file writes, for the denied shell call, and for the
engine's MCP tool (`engine-report`). In Spike 2, with a third-party model through OpenRouter,
only `SessionStart` and `Stop` fired (`archive/docs/16` section 4). With a
first-party model, Copilot CLI supports per-call hook policy.

### 5.4 What changes in the design

| Finding | Change | Where |
|---|---|---|
| Both runtimes report, correct, fall back, wait and stop reliably | R23 (runtimes do not call the engine's tools reliably) mitigated | `docs/10` section 2 |
| Copilot's file tool cannot create the output folder | The engine creates the step's output folder before the agent starts | `docs/10` section 1.2, DR17 |
| Copilot CLI fires `PreToolUse` with a first-party model | The capability statement for Copilot CLI is corrected | `docs/02` section 4.1 |

### 5.5 Limits of the evidence

- Five trials per check bounds the failure rate only loosely; a 5 of 5 result
  is consistent with an underlying failure rate of up to about 45% at 95%
  confidence. The contract tests per runtime (R23's mitigation) remain needed.
- One small task. Long sessions, where the agent may forget the reporting
  preamble, were not tested.
- Different models on the two runtimes (section 5).
- Copilot CLI with a third-party model was not re-tested.

## 6. Open questions

None.
