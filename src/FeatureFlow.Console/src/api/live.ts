import { useEffect, useRef, useState } from "preact/hooks";

// Live data (LOG 459): one change stream (api/events.ts) invalidates keys, and each mounted reader whose keys are
// invalidated reads again. There is no cache beyond mounted components. Keys: "attention", "runs", "run:<id>", and "*"
// for everything.

/** Reads again if any of the keys concern it; resolves when that read has settled. */
type Listener = (keys: ReadonlySet<string>) => Promise<void> | undefined;

const listeners = new Set<Listener>();
let pending = new Set<string>();
let flushed: Promise<void> | undefined;

/** How long invalidations are gathered before readers read again, so a burst of changes is one read. */
const coalesce = 200;

export const runKey = (runId: string): string => `run:${runId}`;

/** Marks the keys changed; resolves when the reads they cause have settled. */
export function invalidate(keys: readonly string[]): Promise<void> {
  for (const key of keys) pending.add(key);
  flushed ??= new Promise(resolve =>
    setTimeout(() => {
      const due = pending;
      pending = new Set();
      flushed = undefined;
      resolve(Promise.allSettled([...listeners].map(listener => listener(due))).then(() => undefined));
    }, coalesce),
  );
  return flushed;
}

export interface Live<T> {
  /** The last value read, kept while the next read is in flight, so the page does not flash and form state survives. */
  data?: T;
  /** The last read's failure, if it failed; data keeps the value before it. */
  error?: Error;
  loading: boolean;
}

/**
 * Reads with `load` on mount and again whenever one of `keys` (or "*") is invalidated. A change of keys, such as another
 * run of the same tree, reads afresh but keeps the last value until that read settles, so a pane that follows the
 * selection does not flash; a reader that must not show another key's data is keyed by it (`key=`) and remounts. Only the
 * latest read's result is kept.
 */
export function useLive<T>(keys: readonly string[], load: () => Promise<T>): Live<T> {
  const [state, setState] = useState<Live<T>>({ loading: true });
  const loader = useRef(load);
  loader.current = load;
  const identity = keys.join("\n");

  useEffect(() => {
    let mounted = true;
    let latest = 0;
    setState(s => ({ data: s.data, loading: true }));
    const read = () => {
      const mine = ++latest;
      setState(s => ({ ...s, loading: true }));
      return loader.current().then(
        data => {
          if (mounted && mine === latest) setState({ data, loading: false });
        },
        (error: unknown) => {
          if (mounted && mine === latest)
            setState(s => ({ data: s.data, error: error instanceof Error ? error : new Error(String(error)), loading: false }));
        },
      );
    };
    const listener: Listener = changed => (changed.has("*") || keys.some(k => changed.has(k)) ? read() : undefined);
    read();
    listeners.add(listener);
    return () => {
      mounted = false;
      listeners.delete(listener);
    };
  }, [identity]);

  return state;
}
