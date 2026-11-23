import { useCallback, useMemo, useState } from "preact/hooks";
import { setPref } from "../../app/prefs";
import { usePopup } from "../../app/popup";
import type { ArtifactTarget, Viewer } from "../../app/viewer";
import { Document } from "./Document";
import { FullSize } from "./FullSize";
import "./viewer.css";

export interface Tab extends ArtifactTarget {
  key: string;
  pinned: boolean;
}

export interface Tabs extends Viewer {
  tabs: readonly Tab[];
  active?: string;
  activate(key: string): void;
  pin(key: string): void;
  close(key: string): void;
  full(tab: Tab): void;
}

const keyOf = (t: ArtifactTarget) => `${t.runId}\n${t.reference}`;

/**
 * The viewer's tabs (design-v2 s4.4), kept by the workspace so they outlive the pane being closed: an artifact opens in the
 * one preview tab, replacing what it showed, unless it is already open or is opened pinned; a pinned tab stays until closed.
 * Opening an artifact opens the pane.
 */
export function useTabs(): Tabs {
  const [tabs, setTabs] = useState<readonly Tab[]>([]);
  const [active, setActive] = useState<string>();
  const popups = usePopup();

  const full = useCallback(
    (t: ArtifactTarget) => popups.open(<FullSize runId={t.runId} reference={t.reference} />, { label: t.name ?? t.reference, wide: true }),
    [popups],
  );

  const openArtifact = useCallback(
    (target: ArtifactTarget, how: { pin?: boolean; full?: boolean } = {}) => {
      if (how.full) return full(target);
      const key = keyOf(target);
      setPref("rightCollapsed", false);
      setTabs(list => {
        const at = list.findIndex(t => t.key === key);
        if (at >= 0) return how.pin ? list.map((t, i) => (i === at ? { ...t, pinned: true } : t)) : list;
        const tab: Tab = { ...target, key, pinned: how.pin ?? false };
        const preview = list.findIndex(t => !t.pinned);
        return preview >= 0 && !tab.pinned ? list.map((t, i) => (i === preview ? tab : t)) : [...list, tab];
      });
      setActive(key);
    },
    [full],
  );

  return useMemo(
    () => ({
      tabs,
      active,
      openArtifact,
      activate: setActive,
      pin: (key: string) => setTabs(list => list.map(t => (t.key === key ? { ...t, pinned: !t.pinned } : t))),
      close: (key: string) => {
        const at = tabs.findIndex(t => t.key === key);
        const rest = tabs.filter(t => t.key !== key);
        setTabs(rest);
        if (active === key) setActive(rest[Math.min(at, rest.length - 1)]?.key);
      },
      full,
    }),
    [tabs, active, openArtifact, full],
  );
}

/** The right pane: a tab per artifact (the preview's name in italics), 📌 to pin, ✕ to close, double-click or ⤢ for full size. */
export function ViewerPane({ tabs }: { tabs: Tabs }) {
  const current = tabs.tabs.find(t => t.key === tabs.active);
  return (
    <div class="ff-viewer-pane">
      <div class="ff-vtabs">
        <div class="ff-vtabs-list" role="tablist" aria-label="Open artifacts">
          {tabs.tabs.map(t => (
            <div
              key={t.key}
              role="tab"
              tabIndex={0}
              aria-selected={t.key === tabs.active}
              class={["ff-vtab", t.pinned ? "ff-vtab-pinned" : "ff-vtab-preview", t.key === tabs.active ? "ff-vtab-on" : ""].join(" ")}
              title={t.pinned ? t.name : `${t.name ?? t.reference} (preview: the next artifact replaces it)`}
              onClick={() => tabs.activate(t.key)}
              onDblClick={() => tabs.full(t)}
              onKeyDown={e => e.key === "Enter" && tabs.activate(t.key)}
            >
              <span class="ff-vtab-name">{t.name ?? t.reference}</span>
              <button
                type="button"
                class="ff-vtab-pin"
                aria-label={t.pinned ? `Unpin ${t.name ?? t.reference}` : `Pin ${t.name ?? t.reference}`}
                aria-pressed={t.pinned}
                onClick={e => {
                  e.stopPropagation();
                  tabs.pin(t.key);
                }}
              >
                📌
              </button>
              <button
                type="button"
                class="ff-vtab-x"
                aria-label={`Close ${t.name ?? t.reference}`}
                onClick={e => {
                  e.stopPropagation();
                  tabs.close(t.key);
                }}
              >
                ✕
              </button>
            </div>
          ))}
        </div>
        <button type="button" class="ff-iconbtn" aria-label="Open full size" title="Open full size" disabled={!current} onClick={() => current && tabs.full(current)}>
          ⤢
        </button>
        <button type="button" class="ff-iconbtn" aria-label="Close the viewer" title="Close the viewer (the top bar's icon opens it)" onClick={() => setPref("rightCollapsed", true)}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <line x1="15" y1="4" x2="15" y2="20" />
          </svg>
        </button>
      </div>
      <div class="ff-vbody" role="tabpanel">
        {current ? (
          <Document key={current.key} runId={current.runId} reference={current.reference} />
        ) : (
          <p class="ff-muted">
            Open an input or output from the middle: it shows here in a <i>preview</i> tab, which the next one replaces. 📌 keeps a tab; double-click a
            tab, or ⤢, to see it full size.
          </p>
        )}
      </div>
    </div>
  );
}
