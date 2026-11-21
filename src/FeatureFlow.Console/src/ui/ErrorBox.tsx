import "./ErrorBox.css";

/** A failure to show: an ApiError's text, or any error's message. */
export function ErrorBox({ error }: { error: Error | string }) {
  return (
    <p class="ff-error" role="alert">
      {typeof error === "string" ? error : error.message}
    </p>
  );
}
