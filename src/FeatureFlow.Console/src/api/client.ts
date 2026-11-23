import createClient, { type Middleware } from "openapi-fetch";
import { signOut, token } from "../auth/session";
import type { components, paths } from "./schema.gen";

/** A contract type by its name in openapi.json, such as Schema<"WaitView">. */
export type Schema<Name extends keyof components["schemas"]> = components["schemas"][Name];

/** A refusal from the API: its status, the ErrorResponse's text and its list of errors, if any (one error shape, Common.cs). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly errors: readonly string[] = [],
  ) {
    super(message);
  }
}

/** The headers every request to the API carries: the token, when the API needs one. */
export function authHeaders(): Record<string, string> {
  const held = token();
  return held === null ? {} : { Authorization: `Bearer ${held}` };
}

/** The ErrorResponse's text, else the status, and its errors (such as a refused triage's, A10). */
async function refusalOf(response: Response): Promise<{ message: string; errors: string[] }> {
  try {
    const body = (await response.json()) as { error?: unknown; errors?: unknown };
    const errors = Array.isArray(body.errors) ? body.errors.filter((e): e is string => typeof e === "string") : [];
    if (typeof body.error === "string") return { message: body.error, errors };
  } catch {
    // Not an ErrorResponse.
  }
  return { message: `The API answered ${response.status}.`, errors: [] };
}

// Every request carries the token. A 401 means it no longer holds, so the console asks for one again; any other refusal
// is thrown as an ApiError, so a call that returns has its data.
const middleware: Middleware = {
  onRequest({ request }) {
    for (const [name, value] of Object.entries(authHeaders())) request.headers.set(name, value);
    return request;
  },
  async onResponse({ response }) {
    if (response.ok) return response;
    const { message, errors } = await refusalOf(response);
    if (response.status === 401) signOut("Sign in again: the API refused the token.");
    throw new ApiError(response.status, message, errors);
  },
};

// The client over the types generated in every build from the committed openapi.json (LOG 456): a route or contract
// the API no longer has is a type error, and so fails `dotnet build`.
export const api = createClient<paths>({ baseUrl: "" });
api.use(middleware);

/** The data of a call through `api`: refusals were thrown by the middleware, so it is there. */
export async function read<T>(call: Promise<{ data?: T }>): Promise<T> {
  return (await call).data as T;
}
