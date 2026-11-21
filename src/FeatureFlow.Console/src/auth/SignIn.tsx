import { useState } from "preact/hooks";
import { ErrorBox } from "../ui/ErrorBox";
import { signIn } from "./session";
import "./SignIn.css";

/** The token form, shown when GET /me without a token is refused (LOG 458). */
export function SignIn({ refused }: { refused?: string }) {
  const [candidate, setCandidate] = useState("");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string>();

  const submit = async (e: Event) => {
    e.preventDefault();
    setBusy(true);
    setFailed(undefined);
    try {
      await signIn(candidate.trim());
    } catch {
      setFailed("The API cannot be reached.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <form class="ff-sign-in" onSubmit={submit}>
      <h1>Feature Flow</h1>
      <p>This API needs a token. Paste the installation's API token, or an identity provider's token.</p>
      <label>
        API token
        <input type="password" autocomplete="off" value={candidate} onInput={e => setCandidate(e.currentTarget.value)} />
      </label>
      <button type="submit" disabled={busy || candidate.trim() === ""}>
        Sign in
      </button>
      {(failed ?? refused) && <ErrorBox error={(failed ?? refused)!} />}
    </form>
  );
}
