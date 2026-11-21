import { signOut, type Me } from "../auth/session";
import { useAttention } from "./attention";
import * as links from "./links";
import "./Header.css";

/** The actor, the screens and how many waits need the caller; Sign out only when the API needs a token. */
export function Header({ me, signedInWithToken }: { me: Me; signedInWithToken: boolean }) {
  const count = useAttention().data?.waits.length;
  return (
    <header class="ff-header">
      <a class="ff-title" href={links.needsMe()}>
        Feature Flow
      </a>
      <nav>
        <a href={links.needsMe()}>Needs me{count === undefined ? "" : ` (${count})`}</a>
        <a href={links.runs()}>Runs</a>
        <a href={links.start()}>Start a run</a>
      </nav>
      <span class="ff-actor">{signedInWithToken ? me.actor : `${me.actor} (no sign-in)`}</span>
      {signedInWithToken && (
        <button type="button" onClick={() => signOut()}>
          Sign out
        </button>
      )}
    </header>
  );
}
