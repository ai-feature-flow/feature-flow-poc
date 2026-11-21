import type { Me } from "../auth/session";
import { ArtifactViewer } from "../screens/artifacts/Viewer";
import { NeedsMe } from "../screens/needs-me/NeedsMe";
import { RunPage } from "../screens/run/RunPage";
import { RunList } from "../screens/runs/RunList";
import { StartRun } from "../screens/start/StartRun";
import { AttentionProvider } from "./attention";
import { Header } from "./Header";
import * as links from "./links";
import { useRoute, type Route } from "./router";

// Every route is here (LOG 463), so a screen task edits only its own folder under screens/.
function screen(route: Route) {
  switch (route.screen) {
    case "needs-me":
      return <NeedsMe />;
    case "runs":
      return <RunList />;
    case "run":
      return <RunPage key={route.runId} runId={route.runId} />;
    case "artifact":
      return <ArtifactViewer key={`${route.runId}\n${route.reference}`} runId={route.runId} reference={route.reference} />;
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

export function App({ me, signedInWithToken }: { me: Me; signedInWithToken: boolean }) {
  const route = useRoute();
  return (
    <AttentionProvider>
      <Header me={me} signedInWithToken={signedInWithToken} />
      <main>{screen(route)}</main>
    </AttentionProvider>
  );
}
