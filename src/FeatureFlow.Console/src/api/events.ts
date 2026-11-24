import { signOut } from "../auth/session";
import { authHeaders } from "./client";
import { invalidate, runKey, stepKey } from "./live";

// GET /events read with fetch streaming, not EventSource, because the route authenticates through the Authorization
// header, which EventSource cannot send (LOG 459). One code path serves every sign-in mode.

/** No bytes for this long (three of the server's heartbeats) means the connection is dead. */
const silence = 45_000;
const longestWait = 30_000;

interface Frame {
  id?: string;
  event?: string;
  data: string;
  retry?: number;
}

/** The server-sent event frames in `text`, and what is left over: an incomplete frame. */
function frames(text: string): { complete: Frame[]; rest: string } {
  const blocks = text.replace(/\r\n?/g, "\n").split("\n\n");
  const rest = blocks.pop() ?? "";
  const complete = blocks.map(block => {
    const frame: Frame = { data: "" };
    const data: string[] = [];
    for (const line of block.split("\n")) {
      if (line === "" || line.startsWith(":")) continue;
      const colon = line.indexOf(":");
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? "" : line.slice(colon + 1).replace(/^ /, "");
      if (field === "id") frame.id = value;
      else if (field === "event") frame.event = value;
      else if (field === "data") data.push(value);
      else if (field === "retry" && /^\d+$/.test(value)) frame.retry = Number(value);
    }
    frame.data = data.join("\n");
    return frame;
  });
  return { complete, rest };
}

function apply(frame: Frame): void {
  switch (frame.event) {
    case "reset":
      // The server no longer holds the changes since our last one: read everything again.
      invalidate(["*"]);
      return;
    case "run_changed":
    case "wait_opened":
    case "wait_closed": {
      const change = JSON.parse(frame.data) as { run_id: string };
      invalidate(["attention", "runs", runKey(change.run_id)]);
      return;
    }
    case "step_changed": {
      // A running agent step's activity: only that step's readers read again.
      const change = JSON.parse(frame.data) as { run_id: string; step_id: string };
      invalidate([stepKey(change.run_id, change.step_id)]);
      return;
    }
  }
}

/**
 * Follows the change stream until the returned function is called: reconnects after `retry` (doubling on repeated
 * failure, to 30 s) with Last-Event-ID; on a connection without one, reads everything again, since changes may have
 * been missed. A 401 signs out. Reads everything again when the page becomes visible.
 */
export function follow(): () => void {
  let stopped = false;
  let controller = new AbortController();
  let lastId: string | null = null;
  let retry = 3000;
  let failures = 0;

  const visible = () => {
    if (document.visibilityState === "visible") invalidate(["*"]);
  };
  document.addEventListener("visibilitychange", visible);

  async function connect(): Promise<"again" | "stop"> {
    controller = new AbortController();
    let watchdog = setTimeout(() => controller.abort(), silence);
    const alive = () => {
      clearTimeout(watchdog);
      watchdog = setTimeout(() => controller.abort(), silence);
    };
    try {
      const headers: Record<string, string> = { Accept: "text/event-stream", ...authHeaders() };
      if (lastId !== null) headers["Last-Event-ID"] = lastId;
      const response = await fetch("/events", { headers, signal: controller.signal, cache: "no-store" });
      if (response.status === 401) {
        signOut("Sign in again: the API refused the token.");
        return "stop";
      }
      if (!response.ok || response.body === null) {
        failures++;
        return "again";
      }
      failures = 0;
      // On a connection with no Last-Event-ID, changes may have been missed: read everything again. Once that has
      // settled, every later change arrives over this stream, which <html data-live="connected"> says (tests wait for it).
      if (lastId === null)
        void invalidate(["*"]).then(() => {
          if (!stopped) document.documentElement.dataset.live = "connected";
        });
      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      let buffer = "";
      for (;;) {
        const { value, done } = await reader.read();
        if (done) return "again";
        alive();
        const { complete, rest } = frames(buffer + value);
        buffer = rest;
        for (const frame of complete) {
          if (frame.id !== undefined) lastId = frame.id;
          if (frame.retry !== undefined) retry = frame.retry;
          apply(frame);
        }
      }
    } catch {
      failures++;
      return "again";
    } finally {
      clearTimeout(watchdog);
    }
  }

  void (async () => {
    while (!stopped) {
      if ((await connect()) === "stop" || stopped) return;
      const wait = Math.min(retry * 2 ** Math.max(0, failures - 1), longestWait);
      await new Promise(resolve => setTimeout(resolve, wait));
    }
  })();

  return () => {
    stopped = true;
    controller.abort();
    delete document.documentElement.dataset.live;
    document.removeEventListener("visibilitychange", visible);
  };
}
