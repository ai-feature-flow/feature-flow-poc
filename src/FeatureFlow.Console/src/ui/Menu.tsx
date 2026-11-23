import { useEffect, useRef, useState } from "preact/hooks";
import "./ui.css";

export interface MenuItem {
  label: string;
  onSelect: () => void;
  disabled?: boolean;
  /** Why a disabled item is disabled, shown under it. */
  note?: string;
}

/** A button that opens a list of actions ("Steer ▾", "Run actions ▾"); it closes on a choice, a click outside or Escape. */
export function Menu({ label, items }: { label: string; items: readonly MenuItem[] }) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", outside);
    return () => document.removeEventListener("mousedown", outside);
  }, [open]);

  return (
    <span
      ref={root}
      class="ff-menu"
      onKeyDown={e => {
        if (open && e.key === "Escape") {
          // Closes the menu only, not a popup it sits in.
          e.stopPropagation();
          setOpen(false);
        }
      }}
    >
      <button type="button" class="ff-btn" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(o => !o)}>
        {label} ▾
      </button>
      {open && (
        <ul class="ff-menu-list" role="menu" aria-label={label}>
          {items.map(item => (
            <li key={item.label} role="none">
              <button
                type="button"
                role="menuitem"
                disabled={item.disabled}
                onClick={() => {
                  setOpen(false);
                  item.onSelect();
                }}
              >
                {item.label}
                {item.note && <small class="ff-menu-note">{item.note}</small>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </span>
  );
}
