import { useState } from "preact/hooks";
import type { Schema } from "../../api/client";
import { Fold } from "../../ui/Fold";
import { Markdown } from "../../ui/Markdown";

type Turns = Schema<"TranscriptTurns">;
type Turn = Schema<"TranscriptTurn">;

const runtimes: Record<Turns["runtime"], string> = { copilot_cli: "Copilot CLI", claude_code: "Claude Code" };
const number = new Intl.NumberFormat("en-GB");

/** A length of time as "4 min 12 s", "26.8 s" or "640 ms". */
function duration(ms: number) {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  const minutes = Math.floor(ms / 60_000);
  return `${minutes} min ${Math.round((ms % 60_000) / 1000)} s`;
}

/** How long after the session started `at` was, as "+1:05"; empty where either time is unknown. */
function offset(started: string | null | undefined, at: string | null | undefined) {
  if (!started || !at) return "";
  const seconds = Math.max(0, Math.round((Date.parse(at) - Date.parse(started)) / 1000));
  return `+${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** How much of a tool's arguments or output is shown before "Show all". */
const clipLines = 40;
const clipChars = 4000;

/** Text in a monospace block, cut to its first lines with a button for the rest, so a long output costs nothing until asked for. */
function Clipped({ text, label }: { text: string; label: string }) {
  const [all, setAll] = useState(false);
  const lines = text.split("\n");
  const long = lines.length > clipLines || text.length > clipChars;
  const shown = all || !long ? text : lines.slice(0, clipLines).join("\n").slice(0, clipChars);
  return (
    <div class="ff-tx-block">
      <div class="ff-tx-label">{label}</div>
      <pre class="ff-viewer-text ff-tx-pre">{shown}</pre>
      {long && (
        <button type="button" class="ff-linkbtn" onClick={() => setAll(a => !a)}>
          {all ? "Show less" : `Show all (${number.format(lines.length)} lines)`}
        </button>
      )}
    </div>
  );
}

/** A tool's arguments, one block per argument: text as it is, so a command's lines read as lines; anything else as indented JSON. */
function Arguments({ json }: { json: string }) {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return <Clipped label="Arguments" text={json} />;
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) return <Clipped label="Arguments" text={json} />;
  return (
    <>
      {Object.entries(value).map(([name, v]) => (
        <Clipped key={name} label={name} text={typeof v === "string" ? v : JSON.stringify(v, null, 2)} />
      ))}
    </>
  );
}

/** The first line of `text`, its heading marks dropped and cut short, for a folded turn's summary. */
function firstLine(text: string | null | undefined, size = 100) {
  const line = ((text ?? "").trim().split("\n")[0] ?? "").replace(/^#+\s+/, "");
  return line.length > size ? `${line.slice(0, size)}…` : line;
}

function Who({ turn, started }: { turn: Turn; started: string | null | undefined }) {
  const label = { prompt: "Prompt", message: "Agent", reasoning: "Reasoning", tool: "Tool", notice: "Notice", error: "Error", result: "Result" }[turn.kind];
  return (
    <div class="ff-tx-who">
      <span>{label}</span>
      {turn.at && (
        <time class="ff-tx-when" dateTime={turn.at} title={new Date(turn.at).toLocaleString("en-GB")}>
          {offset(started, turn.at)}
        </time>
      )}
    </div>
  );
}

function Body({ turn }: { turn: Turn }) {
  switch (turn.kind) {
    case "prompt":
      return <Fold summary={firstLine(turn.text) || "The prompt"}>{() => <Markdown source={turn.text ?? ""} />}</Fold>;
    case "message":
      return <Markdown source={turn.text ?? ""} />;
    case "reasoning":
      return <Fold summary={firstLine(turn.text)}>{() => <p class="ff-text ff-tx-reasoning">{turn.text}</p>}</Fold>;
    case "tool": {
      const tool = turn.tool!;
      return (
        <Fold
          summary={
            <span class="ff-tx-call">
              <code class="ff-tx-name">{tool.name}</code> <span class="ff-tx-summary">{tool.summary ?? firstLine(tool.arguments, 80)}</span>
              {turn.failed && <span class="ff-tx-failed">failed</span>}
            </span>
          }
        >
          {() => (
            <>
              {tool.arguments && <Arguments json={tool.arguments} />}
              {tool.output !== null && tool.output !== undefined ? <Clipped label="Result" text={tool.output} /> : <p class="ff-muted">No result recorded.</p>}
            </>
          )}
        </Fold>
      );
    }
    case "result":
      return (
        <>
          <p class={turn.failed ? "ff-tx-failed" : "ff-muted"}>{turn.failed ? "The session failed." : "The session ended."}</p>
          {turn.text && <Markdown source={turn.text} />}
        </>
      );
    default:
      return <p class={`ff-text${turn.failed ? " ff-tx-failed" : ""}`}>{turn.text}</p>;
  }
}

/**
 * An agent session's transcript as a conversation (console A4), from `GET /runs/{id}/artifacts/{ref}/turns`: a header
 * with the step, runtime, model, duration, calls and tokens, then each turn with who and when. Messages are Markdown through
 * the element renderer; the prompt, reasoning and tool calls are folded, and built only when opened. Every value is text.
 */
export function Transcript({ turns, step }: { turns: Turns; step: string | undefined }) {
  const facts: [string, string | null | undefined][] = [
    ["Step", step],
    ["Runtime", runtimes[turns.runtime]],
    ["Model", turns.model],
    ["Duration", turns.duration_ms !== null && turns.duration_ms !== undefined ? duration(turns.duration_ms) : null],
    ["Tool calls", number.format(turns.tool_calls)],
    ["Model calls", number.format(turns.model_calls)],
    ["Tokens", turns.input_tokens !== null && turns.input_tokens !== undefined
      ? `${number.format(turns.input_tokens)} in · ${number.format(turns.output_tokens ?? 0)} out` : null],
  ];
  return (
    <div class="ff-tx">
      <dl class="ff-tx-head">
        {facts.filter(([, value]) => value).map(([name, value]) => (
          <div key={name}>
            <dt>{name}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      <ol class="ff-tx-turns" aria-label="Turns">
        {turns.turns.map((turn, i) => (
          <li key={i} class="ff-tx-turn" data-kind={turn.kind} data-failed={turn.failed || undefined}>
            <Who turn={turn} started={turns.started} />
            <div class="ff-tx-body">
              <Body turn={turn} />
            </div>
          </li>
        ))}
      </ol>
      {turns.unread.length > 0 && (
        <p class="ff-muted ff-tx-unread">Not shown: {turns.unread.map(u => `${u.type} (${u.count})`).join(", ")}.</p>
      )}
    </div>
  );
}
