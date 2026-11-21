import { useEffect, useState } from "preact/hooks";
import type { components } from "../api/schema.gen";

export type Me = components["schemas"]["MeResponse"];

// Whether to ask for a token is learnt from GET /me without one (LOG 458): 200 means the API needs no sign-in
// (api.auth.mode none); 401 means the token form. A token is checked by GET /me before it is kept, and kept only for
// the tab (sessionStorage).
export type Session =
  | { state: "checking" }
  | { state: "unreachable"; error: string }
  | { state: "signed-out"; refused?: string }
  | { state: "signed-in"; me: Me; token: string | null };

const stored = "ff.token";
let session: Session = { state: "checking" };
const listeners = new Set<(s: Session) => void>();

function set(next: Session) {
  session = next;
  for (const listener of listeners) listener(next);
}

/** The token every request carries, or null when the API needs none. */
export const token = (): string | null => (session.state === "signed-in" ? session.token : null);

function me(candidate: string | null): Promise<Response> {
  return fetch("/me", { headers: candidate === null ? {} : { Authorization: `Bearer ${candidate}` } });
}

/** Finds out whether the API needs a token, and tries the one this tab kept. */
export async function begin(): Promise<void> {
  set({ state: "checking" });
  try {
    const open = await me(null);
    if (open.ok) return set({ state: "signed-in", me: (await open.json()) as Me, token: null });
    if (open.status !== 401) return set({ state: "unreachable", error: `The API answered ${open.status}.` });
    const kept = sessionStorage.getItem(stored);
    if (kept !== null && (await signIn(kept))) return;
    set({ state: "signed-out" });
  } catch {
    set({ state: "unreachable", error: "The API cannot be reached." });
  }
}

/** Signs in with a token if the API accepts it; true if it did. */
export async function signIn(candidate: string): Promise<boolean> {
  const answer = await me(candidate);
  if (!answer.ok) {
    sessionStorage.removeItem(stored);
    set({ state: "signed-out", refused: answer.status === 401 ? "That token was refused." : `The API answered ${answer.status}.` });
    return false;
  }
  sessionStorage.setItem(stored, candidate);
  set({ state: "signed-in", me: (await answer.json()) as Me, token: candidate });
  return true;
}

/** Forgets the token: the person signed out, or the API refused it (401). */
export function signOut(refused?: string): void {
  sessionStorage.removeItem(stored);
  set({ state: "signed-out", refused });
}

export function useSession(): Session {
  const [current, setCurrent] = useState(session);
  useEffect(() => {
    listeners.add(setCurrent);
    setCurrent(session);
    return () => void listeners.delete(setCurrent);
  }, []);
  return current;
}
