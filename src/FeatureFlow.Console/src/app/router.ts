import { useEffect, useState } from "preact/hooks";

// The console's routes (LOG 463; plan s5.2), read from the location's hash. app/links.ts builds them.
//
//   #/ and #/needs                          Needs me
//   #/runs                                  Runs
//   #/runs/{tree}                           the workspace, the tree's root (or the run named) selected
//   #/runs/{tree}/steps/{run}/{step}        the workspace, a step selected (possibly in a child run)
//   #/runs/{tree}/waits/{run}/{wait}        the workspace, the wait's step selected and its popup open
//   #/runs/{tree}/artifacts/{run}/{ref}     the workspace with the artifact in the viewer
//   #/runs/{run}/artifacts/{ref}            slice 1's artifact link: the same, in the run's own tree
//   #/own/{run}                             the run on its own: its own tree, the run as the root (the tree's ↗)
//   #/own/{run}/runs/{child}                 … a run under it selected
//   #/own/{run}/steps|waits|artifacts/…      … as under #/runs/{tree}
//   #/start                                 Start a run

/** What the workspace selects: a run, a step, a wait (its step, with its popup open) or an artifact (in the viewer). */
export type Selection =
  | { kind: "run"; runId: string }
  | { kind: "step"; runId: string; stepId: string }
  | { kind: "wait"; runId: string; waitId: string }
  | { kind: "artifact"; runId: string; reference: string };

export type Route =
  | { screen: "needs-me" }
  | { screen: "runs" }
  | { screen: "workspace"; tree: string; own: boolean; select: Selection }
  | { screen: "start" }
  | { screen: "not-found" };

export function parse(hash: string): Route {
  const path = hash.replace(/^#\/?/, "");
  try {
    const parts = path.split("/").map(decodeURIComponent);
    if (path === "" || path === "needs") return { screen: "needs-me" };
    if (path === "runs") return { screen: "runs" };
    if (path === "start") return { screen: "start" };
    if ((parts[0] !== "runs" && parts[0] !== "own") || parts.some(p => p === "")) return { screen: "not-found" };
    const tree = parts[1];
    const own = parts[0] === "own";
    const at = (select: Selection): Route => ({ screen: "workspace", tree, own, select });
    if (parts.length === 2) return at({ kind: "run", runId: tree });
    if (parts.length === 4 && parts[2] === "artifacts") return at({ kind: "artifact", runId: tree, reference: parts[3] });
    if (parts.length === 4 && parts[2] === "runs" && own) return at({ kind: "run", runId: parts[3] });
    if (parts.length === 5) {
      const runId = parts[3];
      switch (parts[2]) {
        case "steps":
          return at({ kind: "step", runId, stepId: parts[4] });
        case "waits":
          return at({ kind: "wait", runId, waitId: parts[4] });
        case "artifacts":
          return at({ kind: "artifact", runId, reference: parts[4] });
      }
    }
  } catch {
    // A part that is not valid percent-encoding names nothing.
  }
  return { screen: "not-found" };
}

/** The current route, updated on every hash change. */
export function useRoute(): Route {
  const [route, setRoute] = useState(() => parse(location.hash));
  useEffect(() => {
    const changed = () => setRoute(parse(location.hash));
    addEventListener("hashchange", changed);
    return () => removeEventListener("hashchange", changed);
  }, []);
  return route;
}
