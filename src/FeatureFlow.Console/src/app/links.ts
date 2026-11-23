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

/** A run tree's workspace with its root selected: the same route as {@link run}. */
export const tree = (treeId: string): string => run(treeId);

/** A step of a run in a tree (the root or a child run), selected in the workspace. */
export const step = (treeId: string, runId: string, stepId: string): string =>
  `#/runs/${part(treeId)}/steps/${part(runId)}/${part(stepId)}`;

/** A wait of a run in a tree: its step selected and its popup open. */
export const wait = (treeId: string, runId: string, waitId: string): string =>
  `#/runs/${part(treeId)}/waits/${part(runId)}/${part(waitId)}`;

/** One of a run's artifacts or transcripts, by reference, in its own tree's viewer (slice 1's form, kept). */
export const artifact = (runId: string, reference: string): string => `#/runs/${part(runId)}/artifacts/${part(reference)}`;

/** One of a run's artifacts in a tree's viewer: the run may be a child of the tree's root. */
export const treeArtifact = (treeId: string, runId: string, reference: string): string =>
  `#/runs/${part(treeId)}/artifacts/${part(runId)}/${part(reference)}`;

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
