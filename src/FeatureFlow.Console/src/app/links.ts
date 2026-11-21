// The only source of href values (LOG 460). Preact does not refuse a javascript: URL, so a link built from API data is an
// injection path; these build only #/… routes, every part encoded. scripts/text-only.mjs (run by `npm run build` and
// ConsoleTests) fails on any URL attribute whose value is not a call into this file. The routes are app/router.ts's (LOG 463).

const part = encodeURIComponent;

/** Needs me, the default screen. */
export const needsMe = (): string => "#/";

export const runs = (): string => "#/runs";

/** A run's page; the form SlackNotifications.LinkOf builds after the console's address. */
export const run = (runId: string): string => `#/runs/${part(runId)}`;

/** The viewer of one of a run's artifacts or transcripts, by reference. */
export const artifact = (runId: string, reference: string): string => `#/runs/${part(runId)}/artifacts/${part(reference)}`;

export const start = (): string => "#/start";

/**
 * A link to content the console already holds, for a download: an object URL of a Blob it read with the token, because
 * a plain link to the API cannot send the token. The URL is the browser's own (blob:), never text from the API; revoke it
 * with URL.revokeObjectURL once used.
 */
export const download = (content: Blob): string => URL.createObjectURL(content);
