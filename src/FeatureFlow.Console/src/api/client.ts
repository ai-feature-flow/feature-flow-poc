import createClient from "openapi-fetch";
import type { paths } from "./schema.gen";

// The client over the types generated in every build from the committed openapi.json (LOG 456): a route or contract
// the API no longer has is a type error, and so fails `dotnet build`.
export const api = createClient<paths>({ baseUrl: "" });
