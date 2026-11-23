import "./ui.css";

const names = { running: "running", waiting: "waiting on you", breakpoint: "breakpoint" } as const;

/**
 * A tree row's margin mark (design-v2 s4.4): a yellow arrow outlined in black at a running step, a yellow triangle with a
 * black "!" at a step waiting on you, a red dot for a breakpoint. Inline SVG, never markup text.
 */
export function Marker({ kind }: { kind: keyof typeof names }) {
  return (
    <span class={`ff-marker ff-marker-${kind}`} role="img" aria-label={names[kind]}>
      {kind === "running" && (
        <svg viewBox="0 0 30 22" aria-hidden="true">
          <path d="M2 6 H15 V1 L28 11 L15 21 V16 H2 Z" fill="#ffd400" stroke="#000" stroke-width="2" stroke-linejoin="round" />
        </svg>
      )}
      {kind === "waiting" && (
        <svg viewBox="0 0 26 24" aria-hidden="true">
          <path d="M13 1.5 L25 22.5 H1 Z" fill="#ffd400" stroke="#000" stroke-width="2" stroke-linejoin="round" />
          <rect x="11.6" y="8" width="2.8" height="8" fill="#000" />
          <rect x="11.6" y="17.6" width="2.8" height="2.8" fill="#000" />
        </svg>
      )}
      {kind === "breakpoint" && <span class="ff-marker-dot" />}
    </span>
  );
}
