import "./Age.css";

const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["day", 86_400],
  ["hour", 3_600],
  ["minute", 60],
];
const relative = new Intl.RelativeTimeFormat("en-GB", { numeric: "auto" });

/** How long ago `at` (an ISO time) was, with the exact time on hover. */
export function Age({ at }: { at: string }) {
  const seconds = (Date.parse(at) - Date.now()) / 1000;
  const [unit, size] = units.find(([, s]) => Math.abs(seconds) >= s) ?? ["second", 1];
  const text = unit === "second" ? "just now" : relative.format(Math.round(seconds / size), unit);
  return (
    <time class="ff-age" dateTime={at} title={new Date(at).toLocaleString("en-GB")}>
      {text}
    </time>
  );
}
