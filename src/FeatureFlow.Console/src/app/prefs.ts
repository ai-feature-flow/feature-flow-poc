import { useEffect, useState } from "preact/hooks";

// What the console remembers per browser (design-v2 s4.4): the theme, the side panes' widths and whether the right pane
// is collapsed, in localStorage under one key. A value that does not read back as its type is ignored, so a stale or
// edited entry falls back to the default.

export type Theme = "light" | "dark";

export interface Prefs {
  theme: Theme;
  /** The left pane's width in px. */
  leftWidth: number;
  /** The right pane's width in px. */
  rightWidth: number;
  rightCollapsed: boolean;
}

const storageKey = "ff.prefs";

const defaults = (): Prefs => ({
  theme: matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light",
  leftWidth: 360,
  rightWidth: 420,
  rightCollapsed: false,
});

function load(): Prefs {
  const prefs = defaults();
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "{}");
    if (typeof stored !== "object" || stored === null) return prefs;
    const s = stored as Record<string, unknown>;
    if (s.theme === "light" || s.theme === "dark") prefs.theme = s.theme;
    if (typeof s.leftWidth === "number" && Number.isFinite(s.leftWidth)) prefs.leftWidth = s.leftWidth;
    if (typeof s.rightWidth === "number" && Number.isFinite(s.rightWidth)) prefs.rightWidth = s.rightWidth;
    if (typeof s.rightCollapsed === "boolean") prefs.rightCollapsed = s.rightCollapsed;
  } catch {
    // Unreadable storage keeps the defaults.
  }
  return prefs;
}

let current: Prefs = load();
const listeners = new Set<() => void>();

/** Sets the page's theme from the preference: html[data-theme], which app/theme.css keys its tokens on. */
function applyTheme(): void {
  document.documentElement.dataset.theme = current.theme;
}
applyTheme();

export const prefs = (): Readonly<Prefs> => current;

/** Changes one preference, stores it and tells every reader. */
export function setPref<K extends keyof Prefs>(key: K, value: Prefs[K]): void {
  if (current[key] === value) return;
  current = { ...current, [key]: value };
  try {
    localStorage.setItem(storageKey, JSON.stringify(current));
  } catch {
    // A browser that refuses storage keeps the value for this page only.
  }
  if (key === "theme") applyTheme();
  for (const listener of listeners) listener();
}

/** One preference, kept current as it changes. */
export function usePref<K extends keyof Prefs>(key: K): Prefs[K] {
  const [value, setValue] = useState(() => current[key]);
  useEffect(() => {
    const changed = () => setValue(current[key]);
    listeners.add(changed);
    changed();
    return () => void listeners.delete(changed);
  }, [key]);
  return value;
}
