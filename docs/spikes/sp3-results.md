# SP3: the agent reporting contract over MCP on two runtimes

Status: plan and checks pre-registered 2026-11-01, before any run. Results
follow in section 5 once run.

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

Not run yet.

## 6. Open questions

None yet.
