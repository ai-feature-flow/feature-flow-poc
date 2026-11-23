import { type ComponentChildren, createContext } from "preact";
import { useCallback, useContext, useEffect, useMemo, useRef, useState } from "preact/hooks";
import "./popup.css";

// Popups (design-v2 s4.5; plan s5.4): one host for the page, over everything, in a stack so a popup may open another
// (an artifact full size from a decision). The top one closes on Escape, a click outside it, its × or close(); every
// one closes when the route changes. A long popup scrolls inside itself.

export interface PopupOptions {
  /** The dialog's accessible name and title. */
  label: string;
  /** Wider, for an artifact full size. */
  wide?: boolean;
}

export interface Popups {
  open(content: ComponentChildren, options: PopupOptions): void;
  /** Closes the top popup. */
  close(): void;
}

interface Entry extends PopupOptions {
  id: number;
  content: ComponentChildren;
}

const PopupContext = createContext<Popups | null>(null);

/** The popups opened from anywhere under it, and the means to open them (usePopup). */
export function PopupHost({ children }: { children: ComponentChildren }) {
  const [stack, setStack] = useState<Entry[]>([]);
  const next = useRef(0);
  const open = useCallback((content: ComponentChildren, options: PopupOptions) => {
    const id = ++next.current;
    setStack(s => [...s, { ...options, id, content }]);
  }, []);
  const close = useCallback(() => setStack(s => s.slice(0, -1)), []);
  const popups = useMemo(() => ({ open, close }), [open, close]);

  const any = stack.length > 0;
  useEffect(() => {
    if (!any) return;
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        close();
      }
    };
    document.addEventListener("keydown", key);
    return () => document.removeEventListener("keydown", key);
  }, [any, close]);

  useEffect(() => {
    const changed = () => setStack([]);
    addEventListener("hashchange", changed);
    return () => removeEventListener("hashchange", changed);
  }, []);

  return (
    <PopupContext.Provider value={popups}>
      {children}
      {stack.map(entry => (
        <Overlay key={entry.id} entry={entry} close={close} />
      ))}
    </PopupContext.Provider>
  );
}

function Overlay({ entry, close }: { entry: Entry; close: () => void }) {
  const dialog = useRef<HTMLDivElement>(null);
  useEffect(() => dialog.current?.focus(), []);
  return (
    <div
      class="ff-overlay"
      onClick={e => {
        if (e.target === e.currentTarget) close();
      }}
    >
      <div ref={dialog} class={entry.wide ? "ff-popup ff-popup-wide" : "ff-popup"} role="dialog" aria-modal="true" aria-label={entry.label} tabIndex={-1}>
        <header class="ff-popup-head">
          <h2>{entry.label}</h2>
          <button type="button" class="ff-iconbtn" aria-label="Close" title="Close (Esc)" onClick={close}>
            ×
          </button>
        </header>
        <div class="ff-popup-body">{entry.content}</div>
      </div>
    </div>
  );
}

/** Opens and closes popups; only under a PopupHost. */
export function usePopup(): Popups {
  const popups = useContext(PopupContext);
  if (popups === null) throw new Error("usePopup outside a PopupHost");
  return popups;
}
