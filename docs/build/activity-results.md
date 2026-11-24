# Live agent activity results

The console step detail's Activity tab shows an agent step's messages and tool calls while it runs, in the readable
transcript's turn shape (design `build-activity/0-design/design.md`).

## 1. Recording

| Part | Where | What |
|---|---|---|
| Reader | `Providers.Abstractions/AgentTranscripts.cs` | One reader for both runtimes' output, fed as it grows: a partial last line waits for its end; each completed message, reasoning, tool start and finish, notice or error is an entry, capped (message 4,000 characters, output's tail 2,000; `docs/04` s3.1). The runtimes use it live: no turns kept |
| Progress | `AgentRuntimes.cs` (`GetProgressAsync`) | Feeds the output after the cursor and returns the entries. Every attach starts a fresh reader at the start of the output, so entries keep their numbers and re-recording adds nothing; the reader is dropped once the session is collected or stopped |
| Flush | `Engine.Agent/AgentActivities.cs` (`SuperviseAsync`) | Appends the entries at most every 2 s (`ActivityFlush`) and when the session ends; past 500 entries, one `truncated` entry. Best effort: a refused write is logged and retried at the next flush |
| Stores | SQLite, Postgres (schema 5), memory | `activity` keyed by run, step, execution, session and sequence, first write wins; `changes.step_id` for `step_changed` |

Integration test (real CLI in Docker, scripted model, no quota), both runtimes, with a Temporal dev server on a port of
your own: `temporal server start-dev --headless --port 7871 --http-port 7872 --metrics-port 7873 --ui-port 7874`, then
`FF_TEMPORAL_ADDRESS=localhost:7871 dotnet test tests/FeatureFlow.Integration.Tests --filter "FullyQualifiedName~AgentActivityTests"`.
It sees the message and the `echo one` call recorded while the agent sleeps, before the session ends, then reads the
route once it has ended: session 1 is `transcript`, with the `echo one` tool turn. 2 passed (about 40 s).

## 2. Route and frame

`GET /runs/{id}/steps/{step}/activity?execution=N` (a viewer of the run) answers the execution's sessions as turns:
`transcript` once its transcript is stored and readable, else `live` from the activity records. In a live session a
`tool` turn with `output: null` is still running. `GET /events` sends `step_changed` with `run_id` and `step_id` for
each flush. An example: session 2 live, truncated, its `bash` call running.

```json
{"execution":1,"executions":[1],"sessions":[{"session":1,"source":"live","transcript_ref":null,"truncated":false,"turns":[{"kind":"message","at":"2026-11-24T01:00:01+00:00","text":"first session","tool":null,"failed":false}]},{"session":2,"source":"live","transcript_ref":null,"truncated":true,"turns":[{"kind":"message","at":"2026-11-24T01:00:01+00:00","text":"Looking","tool":null,"failed":false},{"kind":"tool","at":"2026-11-24T01:00:02+00:00","text":null,"tool":{"name":"bash","summary":"List","arguments":"{}","output":null},"failed":false},{"kind":"notice","at":"2094-12-12T04:14:07+00:00","text":"More in the transcript once the session ends","tool":null,"failed":false}]}]}
```

The frame: `event: step_changed`, `data: {"sequence":N,"run_id":"pay","kind":"step_changed","wait_id":null,"updated_at":"...","step_id":"build"}`.

## 3. Console

### 3.1 What was built

| Part | Where | What |
|---|---|---|
| Activity tab | `screens/workspace/step/ActivityTab.tsx`, `StepDetail.tsx` | Enabled. Opens first for a running agent step; otherwise Overview stays first. Reads `GET /runs/{id}/steps/{step}/activity` with `useLive` on `step:<run>/<step>` and `run:<run>` |
| Sessions | same | One turn list per session under a divider (session number, `live`, `recorded live` or `transcript`, and "Full transcript" opening the viewer on `transcript_ref` once stored) |
| Turn list | `viewer/Transcript.tsx` (`TurnList`) | The transcript viewer's `<ol>` factored out and shared; the viewer's markup is unchanged. Messages through the existing Markdown renderer; text only |
| Running marker | same, `viewer.css` | A "running" pill on the last tool call with no result in the latest live session of a running step, unless the session was truncated; its body reads "Running…" |
| Auto-scroll | `ActivityTab.tsx`, `Workspace.css` | The sessions scroll in their own box, newest at the bottom. A live session opens at the bottom and follows new activity while the person is within 40 px of it; scrolled up, the box holds its place and shows "New activity ↓", which jumps down. A transcript never moves it |
| Execution picker | `ActivityTab.tsx` | A select of `executions` when the step ran more than once; follows the latest until an older one is picked, and again once the latest is picked back |
| Frames | `api/live.ts` (`stepKey`), `api/events.ts` | `step_changed` invalidates only that step's key (not `runs`, `attention` or the run) |
| Tests | `Console.Tests/ActivityTests.cs`, `FakeEngine.Records.cs` (`AgentStepAsync`, `ActivityAsync`) | Live updates over `step_changed` with the running marker; follow at the bottom and hold when scrolled up; a finished step's transcript turns and its link; the picker. `Screenshots_of_the_activity_tab` (explicit) saves the screenshots |

### 3.2 Live use

Once the activity work is merged to `main` and `~/ff-live/ff.sh update` has run:

1. Start a run whose template has an agent step, such as a ticket on `std:templates/ticket_delivery`, from Start a run
   at `http://127.0.0.1:8580/console`.
2. Open the run and, in the tree, click the running agent step (the yellow arrow; Running in the pills steps to it).
   The Activity tab is selected: the agent's messages and tool calls appear as it works, a few seconds behind, the
   current tool call marked "running".
3. Scroll the activity up while it runs: it stays put and "New activity ↓" appears; click it to go back to the newest.
4. When the session ends, the tab switches to the stored transcript (the divider reads "transcript"); "Full
   transcript" opens it in the viewer.
5. On a step that ran more than once, the Execution select shows each execution; pick an older one to read it.
6. Evidence to record: how far behind the agent the tab runs, anything missing or wrong against the transcript, with
   the run id and a screenshot.
