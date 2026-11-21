import "./Busy.css";

/** Shown while the first read of a screen is in flight. */
export function Busy() {
  return (
    <p class="ff-busy" aria-busy="true">
      Loading…
    </p>
  );
}
