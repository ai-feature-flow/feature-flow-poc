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
  | { screen: "workspace"; tree: string; select: Selection }
  | { screen: "start" }
  | { screen: "not-found" };

export function parse(hash: string): Route {
  const path = hash.replace(/^#\/?/, "");
  try {
    const parts = path.split("/").map(decodeURIComponent);
    if (path === "" || path === "needs") return { screen: "needs-me" };
    if (path === "runs") return { screen: "runs" };
    if (path === "start") return { screen: "start" };
    if (parts[0] !== "runs" || parts.some(p => p === "")) return { screen: "not-found" };
    const tree = parts[1];
    if (parts.length === 2) return { screen: "workspace", tree, select: { kind: "run", runId: tree } };
    if (parts.length === 4 && parts[2] === "artifacts") return { screen: "workspace", tree, select: { kind: "artifact", runId: tree, reference: parts[3] } };
    if (parts.length === 5) {
      const runId = parts[3];
      switch (parts[2]) {
        case "steps":
          return { screen: "workspace", tree, select: { kind: "step", runId, stepId: parts[4] } };
        case "waits":
          return { screen: "workspace", tree, select: { kind: "wait", runId, waitId: parts[4] } };
        case "artifacts":
          return { screen: "workspace", tree, select: { kind: "artifact", runId, reference: parts[4] } };
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
