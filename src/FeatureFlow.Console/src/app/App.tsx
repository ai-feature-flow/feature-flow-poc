import type { Me } from "../auth/session";
import { NeedsMe } from "../screens/needs-me/NeedsMe";
import { RunList } from "../screens/runs/RunList";
import { StartRun } from "../screens/start/StartRun";
import { Workspace } from "../screens/workspace/Workspace";
import { AttentionProvider } from "./attention";
import * as links from "./links";
import { PopupHost } from "./popup";
import { useRoute, type Route } from "./router";
import { TopBar } from "./TopBar";

// Every route is here (LOG 463; plan s5.2), so a screen package edits only its own folder under screens/.
function screen(route: Route) {
  switch (route.screen) {
    case "needs-me":
      return <NeedsMe />;
    case "runs":
      return <RunList />;
    case "workspace":
      return <Workspace key={route.tree} tree={route.tree} select={route.select} />;
    case "start":
      return <StartRun />;
    case "not-found":
      return (
        <p>
          There is no such page. <a href={links.needsMe()}>Go to Needs me</a>.
        </p>
      );
  }
}

/** The shell (plan s5.1): the top bar over the routed screen, and the popups over both. */
export function App({ me, signedInWithToken }: { me: Me; signedInWithToken: boolean }) {
  const route = useRoute();
  return (
    <AttentionProvider>
      <PopupHost>
        <TopBar me={me} signedInWithToken={signedInWithToken} route={route} />
        <main class={route.screen === "workspace" ? "ff-main ff-main-full" : "ff-main"} data-screen={route.screen}>
          {screen(route)}
        </main>
      </PopupHost>
    </AttentionProvider>
  );
}
