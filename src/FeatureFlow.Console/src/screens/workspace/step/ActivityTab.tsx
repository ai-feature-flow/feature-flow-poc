import { useLayoutEffect, useRef, useState } from "preact/hooks";
import { api, read, type Schema } from "../../../api/client";
import { runKey, stepKey, useLive } from "../../../api/live";
import { ArtifactLink } from "../../../app/viewer";
import { ErrorBox } from "../../../ui/ErrorBox";
import { TurnList } from "../../viewer/Transcript";

type Activity = Schema<"StepActivity">;
type Session = Schema<"ActivitySession">;

/** How close to the bottom (px) counts as at it: the list then follows new activity. */
const near = 40;

/** The index of a live session's current tool call: its last one with no result yet; undefined when there is none. */
function current(session: Session): number | undefined {
  for (let i = session.turns.length - 1; i >= 0; i--) {
    const turn = session.turns[i];
    if (turn.kind === "tool" && (turn.tool?.output === null || turn.tool?.output === undefined)) return i;
  }
  return undefined;
}

/**
 * A step's agent activity (design s5; design-v2 s4.4), from `GET /runs/{id}/steps/{step}/activity`: one execution's
 * sessions, each a turn list, live while the session runs and its transcript once stored. It reads again on the step's
 * `step_changed` (new activity) and the run's changes (a transcript stored, the step run again). With more than one
 * execution a picker chooses which; it follows the latest until the person picks an older one.
 */
export function ActivityTab({ runId, stepId, running }: { runId: string; stepId: string; running: boolean }) {
  const [picked, setPicked] = useState<number | null>(null);
  // The picked execution is part of the keys only so that picking another reads afresh; nothing invalidates it.
  const activity = useLive([stepKey(runId, stepId), runKey(runId), `execution:${picked ?? "latest"}`], () =>
    read(api.GET("/runs/{id}/steps/{step}/activity", {
      params: { path: { id: runId, step: stepId }, query: picked === null ? {} : { execution: picked } },
    })),
  );
  const data = activity.data;
  if (!data) return activity.error ? <ErrorBox error={activity.error} /> : <p class="ff-muted">Loading…</p>;
  const latest = Math.max(...data.executions);
  return (
    <>
      {activity.error && <ErrorBox error={activity.error} />}
      {data.executions.length > 1 && (
        <label class="ff-act-pick">
          Execution{" "}
          <select value={data.execution} onChange={e => {
            const chosen = Number(e.currentTarget.value);
            setPicked(chosen === latest ? null : chosen);
          }}>
            {data.executions.map(n => (
              <option key={n} value={n}>
                {n}
                {n === latest ? " (latest)" : ""}
              </option>
            ))}
          </select>
        </label>
      )}
      {data.sessions.length === 0 ? (
        <p class="ff-muted">{data.executions.length === 0 ? "The step has no agent activity." : "No activity recorded yet."}</p>
      ) : (
        <Sessions key={data.execution} activity={data} runId={runId} running={running && data.execution === latest} />
      )}
    </>
  );
}

/**
 * The execution's sessions in a scrolling box, newest at the bottom. While a session is live the box follows new activity
 * if the person was at its bottom; scrolled up, it keeps its place and offers "New activity ↓". A stored transcript never
 * moves it.
 */
function Sessions({ activity, runId, running }: { activity: Activity; runId: string; running: boolean }) {
  const box = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const [behind, setBehind] = useState(false);
  const live = activity.sessions.some(s => s.source === "live");
  // What changes when activity arrives: turns added, or a tool call finished.
  const shape = activity.sessions.map(s => `${s.session}:${s.source}:${s.turns.length}:${s.turns.filter(t => t.tool?.output != null).length}`).join(" ");

  const toBottom = () => {
    const e = box.current;
    if (!e) return;
    e.scrollTop = e.scrollHeight;
    atBottom.current = true;
    setBehind(false);
  };

  useLayoutEffect(() => {
    if (!live) return;
    if (atBottom.current) toBottom();
    else setBehind(true);
  }, [shape]);

  const onScroll = () => {
    const e = box.current!;
    atBottom.current = e.scrollHeight - e.scrollTop - e.clientHeight <= near;
    if (atBottom.current) setBehind(false);
  };

  const last = activity.sessions[activity.sessions.length - 1];
  return (
    <div class="ff-act">
      <div class="ff-act-scroll" ref={box} onScroll={onScroll} aria-label="Activity" role="region">
        {activity.sessions.map(s => (
          <section key={s.session} class="ff-act-session" aria-label={`Session ${s.session}`}>
            <header class="ff-act-divider">
              <span>Session {s.session}</span>
              <span class="ff-muted">{s.source === "live" ? (running ? "live" : "recorded live") : "transcript"}</span>
              {s.transcript_ref && (
                <ArtifactLink target={{ runId, reference: s.transcript_ref, name: `transcript:${s.session} · exec ${activity.execution}` }}>
                  Full transcript
                </ArtifactLink>
              )}
            </header>
            {s.turns.length === 0 ? (
              <p class="ff-muted">Nothing yet.</p>
            ) : (
              <TurnList
                turns={s.turns}
                started={s.turns.find(t => t.at)?.at}
                running={running && s === last && s.source === "live" ? current(s) : undefined}
                label={`Session ${s.session} turns`}
              />
            )}
          </section>
        ))}
      </div>
      {behind && (
        <button type="button" class="ff-btn ff-act-new" onClick={toBottom}>
          New activity ↓
        </button>
      )}
    </div>
  );
}
