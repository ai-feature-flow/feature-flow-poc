import { render } from "preact";
import { useEffect } from "preact/hooks";
import { follow } from "./api/events";
import { App } from "./app/App";
import { begin, useSession } from "./auth/session";
import { SignIn } from "./auth/SignIn";
import { Busy } from "./ui/Busy";
import { ErrorBox } from "./ui/ErrorBox";
// The theme's tokens, and the preference that sets html[data-theme] before the first render (no flash of the other theme).
import "./app/theme.css";
import "./app/prefs";
import "./ui/ui.css";
import "./main.css";

// API data reaches the page only as text: JSX children, never markup, and links only from app/links.ts (LOG 460).
// ConsoleTests.The_console_sources_never_build_markup_or_links_from_data enforces it.

/** The sign-in gate: the console once the API accepts the caller, with the change stream followed while it does. */
function Console() {
  const session = useSession();
  const signedIn = session.state === "signed-in";
  useEffect(() => (signedIn ? follow() : undefined), [signedIn]);

  switch (session.state) {
    case "checking":
      return <Busy />;
    case "unreachable":
      return (
        <>
          <ErrorBox error={session.error} />
          <button type="button" onClick={() => void begin()}>
            Try again
          </button>
        </>
      );
    case "signed-out":
      return <SignIn refused={session.refused} />;
    case "signed-in":
      return <App me={session.me} signedInWithToken={session.token !== null} />;
  }
}

render(<Console />, document.getElementById("app")!);
void begin();
