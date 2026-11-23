import type { RefObject } from "preact";
import { useEffect, useRef, useState } from "preact/hooks";
import "./ui.css";

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/**
 * The drag handle on a side pane's inner border (design-v2 s4.4). `pane` is which side the pane it sizes is on: dragging
 * towards the middle widens it. Arrow keys move it 16 px. The caller stores the width (app/prefs.ts) and applies it with
 * {@link useCssVar}.
 */
export function Splitter({ label, pane, value, min, max, onChange }: {
  label: string;
  pane: "left" | "right";
  value: number;
  min: number;
  max: number;
  onChange: (width: number) => void;
}) {
  const drag = useRef<{ x: number; start: number } | null>(null);
  const [active, setActive] = useState(false);
  const sign = pane === "left" ? 1 : -1;

  return (
    <div
      class={active ? "ff-splitter ff-splitter-active" : "ff-splitter"}
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      onPointerDown={e => {
        e.currentTarget.setPointerCapture(e.pointerId);
        drag.current = { x: e.clientX, start: value };
        setActive(true);
      }}
      onPointerMove={e => {
        if (drag.current) onChange(clamp(drag.current.start + sign * (e.clientX - drag.current.x), min, max));
      }}
      onPointerUp={() => {
        drag.current = null;
        setActive(false);
      }}
      onKeyDown={e => {
        const step = e.key === "ArrowRight" ? 16 : e.key === "ArrowLeft" ? -16 : 0;
        if (step === 0) return;
        e.preventDefault();
        onChange(clamp(value + sign * step, min, max));
      }}
    />
  );
}

/** Sets a CSS custom property on an element through the CSSOM (`--left: 360px`): the console's policy allows no style attribute text. */
export function useCssVar(element: RefObject<HTMLElement>, name: `--${string}`, px: number): void {
  useEffect(() => element.current?.style.setProperty(name, `${px}px`), [element, name, px]);
}
