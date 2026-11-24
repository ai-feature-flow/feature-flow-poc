# Live agent activity results

The console step detail's Activity tab shows an agent step's messages and tool calls while it runs, in the readable
transcript's turn shape (design `build-activity/0-design/design.md`). Builders A (runtime, recording) and B (route,
`step_changed` frames) add their sections here.

## 3. Console

### 3.1 What was built

| Part | Where | What |
|---|---|---|
| Activity tab | `screens/workspace/step/ActivityTab.tsx`, `StepDetail.tsx` | Enabled. Opens first for a running agent step; otherwise Overview stays first. Reads `GET /runs/{id}/steps/{step}/activity` with `useLive` on `step:<run>/<step>` and `run:<run>` |
| Sessions | same | One turn list per session under a divider (session number, `live`, `recorded live` or `transcript`, and "Full transcript" opening the viewer on `transcript_ref` once stored) |
| Turn list | `viewer/Transcript.tsx` (`TurnList`) | The transcript viewer's `<ol>` factored out and shared; the viewer's markup is unchanged. Messages through the existing Markdown renderer; text only |
| Running marker | same, `viewer.css` | A "running" pill on the last tool call with no result in the latest live session of a running step |
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
