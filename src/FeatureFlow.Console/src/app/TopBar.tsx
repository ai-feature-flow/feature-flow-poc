import { signOut, type Me } from "../auth/session";
import { useAttention } from "./attention";
import * as links from "./links";
import { setPref, usePref } from "./prefs";
import type { Route } from "./router";
import "./TopBar.css";

/**
 * The top bar (design-v2 s4.1, mock-ups 02 and 06): Feature Flow · Needs me n (red) · Runs · Start a run · Templates and
 * Settings (greyed: later) · the actor (Sign out when the API needs a token) · theme toggle · on the workspace, the
 * right-pane toggle (the workspace reads the same preference).
 */
export function TopBar({ me, signedInWithToken, route }: { me: Me; signedInWithToken: boolean; route: Route }) {
  const count = useAttention().data?.waits.length;
  const theme = usePref("theme");
  const rightCollapsed = usePref("rightCollapsed");
  const current = (screen: Route["screen"]) => (route.screen === screen ? "page" : undefined);

  return (
    <header class="ff-top">
      <a class="ff-title" href={links.needsMe()}>
        Feature Flow
      </a>
      <nav class="ff-top-nav" aria-label="Screens">
        <a class="ff-needsme" href={links.needs()} aria-current={current("needs-me")}>
          Needs me{count !== undefined && <span class="ff-needsme-count"> {count}</span>}
        </a>
        <a href={links.runs()} aria-current={current("runs")}>
          Runs
        </a>
        <a href={links.start()} aria-current={current("start")}>
          Start a run
        </a>
        <span class="ff-later" aria-disabled="true" title="A later follow-up">
          Templates
        </span>
        <span class="ff-later" aria-disabled="true" title="Not in this build">
          Settings
        </span>
      </nav>
      <span class="ff-top-right">
        <span class="ff-actor">{signedInWithToken ? me.actor : `${me.actor} (no sign-in)`}</span>
        {signedInWithToken && (
          <button type="button" class="ff-top-button" onClick={() => signOut()}>
            Sign out
          </button>
        )}
        <button
          type="button"
          class="ff-iconbtn"
          aria-label="Dark theme"
          aria-pressed={theme === "dark"}
          title="Dark / light theme (Nord)"
          onClick={() => setPref("theme", theme === "dark" ? "light" : "dark")}
        >
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
            <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
          </svg>
        </button>
        {route.screen === "workspace" && (
          <button
            type="button"
            class="ff-iconbtn"
            aria-label="Right pane"
            aria-pressed={!rightCollapsed}
            title="Show or hide the right pane"
            onClick={() => setPref("rightCollapsed", !rightCollapsed)}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
              <rect x="3" y="4" width="18" height="16" rx="2" />
              <line x1="15" y1="4" x2="15" y2="20" />
            </svg>
          </button>
        )}
      </span>
    </header>
  );
}
