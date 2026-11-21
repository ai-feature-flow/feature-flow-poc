import { useEffect, useState } from "preact/hooks";

// The console's routes (LOG 463), read from the location's hash. app/links.ts builds them.
export type Route =
  | { screen: "needs-me" }
  | { screen: "runs" }
  | { screen: "run"; runId: string }
  | { screen: "artifact"; runId: string; reference: string }
  | { screen: "start" }
  | { screen: "not-found" };

export function parse(hash: string): Route {
  const path = hash.replace(/^#\/?/, "");
  const parts = path.split("/");
  try {
    if (path === "") return { screen: "needs-me" };
    if (path === "runs") return { screen: "runs" };
    if (path === "start") return { screen: "start" };
    if (parts[0] === "runs" && parts.length === 2 && parts[1] !== "") return { screen: "run", runId: decodeURIComponent(parts[1]) };
    if (parts[0] === "runs" && parts.length === 4 && parts[2] === "artifacts" && parts[1] !== "" && parts[3] !== "")
      return { screen: "artifact", runId: decodeURIComponent(parts[1]), reference: decodeURIComponent(parts[3]) };
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
