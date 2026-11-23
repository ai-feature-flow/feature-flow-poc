// The only source of href values (LOG 460). Preact does not refuse a javascript: URL, so a link built from API data is an
// injection path; these build only #/… routes, every part encoded. scripts/text-only.mjs (run by `npm run build` and
// ConsoleTests) fails on any URL attribute whose value is not a call into this file. The routes are app/router.ts's (LOG 463).

const part = encodeURIComponent;

/** Needs me, the default screen. */
export const needsMe = (): string => "#/";

/** Needs me by name (#/needs), as the top bar links it. */
export const needs = (): string => "#/needs";

export const runs = (): string => "#/runs";

/** A run's workspace; the form SlackNotifications.LinkOf builds after the console's address. A child run's opens in its root's tree. */
export const run = (runId: string): string => `#/runs/${part(runId)}`;

/** A run tree's workspace with its root selected: the same route as {@link run}; with `own`, the run's own tree ({@link own}). */
export const tree = (treeId: string, own = false): string => (own ? `#/own/${part(treeId)}` : run(treeId));

/** A run on its own: its own tree, the run as the root, whatever runs above it (the tree's ↗). */
export const own = (runId: string): string => tree(runId, true);

/** A run of a tree selected in the workspace, the tree shown from `treeId` (with `own`, as that run's own tree). */
export const treeRun = (treeId: string, runId: string, own = false): string =>
  own ? `${tree(treeId, true)}/runs/${part(runId)}` : run(runId);

/** A step of a run in a tree (the root or a child run), selected in the workspace. */
export const step = (treeId: string, runId: string, stepId: string, own = false): string =>
  `${tree(treeId, own)}/steps/${part(runId)}/${part(stepId)}`;

export const start = (): string => "#/start";

/**
 * A link out of the console, such as a pull request, a check or a tracker issue: the URL when it is https, otherwise a
 * harmless link to Needs me, so text from the API never becomes a javascript: or data: link.
 */
export function external(url: string | null | undefined): string {
  try {
    if (url && new URL(url).protocol === "https:") return url;
  } catch {
    // Not a URL: no link out.
  }
  return needsMe();
}

/**
 * A link to content the console already holds, for a download: an object URL of a Blob it read with the token, because
 * a plain link to the API cannot send the token. The URL is the browser's own (blob:), never text from the API; revoke it
 * with URL.revokeObjectURL once used.
 */
export const download = (content: Blob): string => URL.createObjectURL(content);
