# SP2: `ask_human` across an agent worker restart

Status: plan and checks pre-registered 2026-11-01, before any run. Results
follow in section 5 once run.

## 1. Claim under test

An agent step can ask a person a question (`ask_human`, `docs/04` section 3.4)
and wait, possibly for hours. The engine's side of that wait is served by the
agent worker pool (`docs/02` section 4.2, RW17). Worker replicas keep nothing
in memory: an open question lives in workflow state and the record store, and
when a worker restarts, the client in the sandbox reconnects to another replica,
which resumes the wait.

The claim is that **a question survives the loss of the replica serving it**:
the agent receives the person's answer, once, with no duplicate question, and
carries on. Risk R22 (the answer is lost across a worker restart) in `docs/10`
section 2.

The spike also re-tests R19 on the agent worker: a hook call that cannot reach
any replica must hold and retry, not read as a denial.

## 2. What is built

`spikes/05-ask-human`. Temporal dev server on port 7733, task queue `sp2`.

| Part | Stands in for | Behaviour |
|---|---|---|
| Step workflow | The run's state for one agent step | Opens a question decision on a signal (idempotent by question id). Records an answer from an Update, rejecting an unknown question or an answer after timeout. A timer times the question out. Records the agent's report from a signal. After an answer or a timeout, an activity writes it to the record store |
| Record store | The record store | One JSON file per question in a shared directory, written by atomic rename |
| Replica (.NET, ASP.NET Core) | One agent worker replica serving the engine endpoint and the hook endpoint | Stateless. `POST /questions` signals the workflow and writes the question file if absent. `GET /questions/{id}` long-polls the record store for up to 25 s. `POST /report` signals the workflow. `POST /hooks/pre_tool_use` allows any tool call except `git push`. Two replicas on separate ports |
| Sandbox client (Python, stdio MCP server) | The client in the sandbox (`docs/02` section 4.2) | Claude Code's MCP server for the tools `ask_human` and `report`. It assigns the question id once, then posts and long-polls, moving to the other replica on any connection error, for up to 10 minutes of failures in a row. It logs every attempt |
| Hook client (Python) | The hook command in the sandbox | Posts each PreToolUse call to a replica; holds and retries across replicas for up to 120 s; then stops the session (`continue: false`), never denies |
| Agent | The agent | `claude -p`, model `haiku`, started by the harness rather than a replica, so it outlives them as a sandbox does. `MCP_TOOL_TIMEOUT` set to 2 hours. Prompt: ask the question, write the answer verbatim to `answer.txt`, then report outcome `done` |
| Orchestration worker and harness | The orchestration worker and the API | Run the workflow and activity; start, kill and restart replicas; answer as the person by Update |

The answer the harness sends is a fresh nonce (for example `teal-58213`), not
one of the suggested options, so the agent cannot produce it without receiving
it.

## 3. Scenarios and pre-registered checks

| # | Scenario | Runs |
|---|---|---|
| S1 | Rolling restart: once the question is open, kill the replica that opened it, start it again, then kill the other. Answer 10 s later | 3 |
| S2 | Full outage: once the question is open, kill both replicas. Answer while none is running. Start one 60 s later | 3 |
| S3 | Timeout: question timeout 90 s, no answer. Kill the serving replica at 30 s and restart it. Then try to answer after the timeout | 1 |
| S4 | Hook during an outage (R19): both replicas down when the agent starts, so its first tool call's hook cannot reach one. Start a replica 30 s after the agent's first hook attempt | 2 |
| L1 | Long wait: as S1, but answer 20 minutes after the question opens, with a replica restart at 10 minutes | 1 |

**Exercised** (without these, a pass proves nothing):

| # | Check | Threshold |
|---|---|---|
| E1 | In S1, S2, L1: the replica process that opened the question was killed before the answer was delivered, and the sandbox client logged at least one connection failure while waiting | Every run |
| E2 | In S2: the answer was recorded by the workflow while no replica was running | Every run |
| E3 | In S4: the hook client logged at least one failed attempt before it got a decision | Every run |

**Claim checks:**

| # | Check | Pass |
|---|---|---|
| C1 | The agent got the answer | S1, S2, L1: `answer.txt` holds exactly the nonce sent |
| C2 | One question | The workflow opened exactly one question; the Claude Code transcript has exactly one `ask_human` call |
| C3 | No errors reached the agent | No tool result in the transcript is an error |
| C4 | The step finished | The workflow recorded the report with outcome `done`, and the agent process exited 0 |
| C5 | Timeout (S3) | The agent was told no answer came and wrote `NO ANSWER` to `answer.txt`; the answer sent after the timeout was rejected |
| C6 | Hook not denied (S4) | The first tool call ran (its file exists) and the transcript shows no denial |

**Negative controls.** Each must make the named check fail:

| # | Change | Must fail |
|---|---|---|
| N1 | The sandbox client does not reconnect: the first connection error is returned to the agent as an error | C1 or C3 in an S1 run. Shows the kill really breaks the wait |
| N2 | The hook client fails closed: an unreachable replica is a denial | C6 in an S4 run. Shows the outage really reaches the hook |

## 4. Out of scope

- A worker re-attaching to a running sandbox, and session resume (`docs/02`
  section 4.3); here the harness owns the agent process.
- The signed per-step token and its policy; replicas trust the caller.
- Copilot CLI (SP3 covers the reporting contract on both runtimes).
- What Claude Code does if it talks to a replica directly over HTTP MCP, with
  no client in the sandbox. The design has a client in the sandbox, so that is
  what is tested.

## 5. Results

Not run yet.

## 6. Open questions

None yet.
