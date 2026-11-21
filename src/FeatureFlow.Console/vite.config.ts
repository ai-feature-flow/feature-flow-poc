import { defineConfig } from "vite";

// The console is served by FeatureFlow.Api at /console as hashed assets (LOG 453, 454). Its policy forbids inline
// script and data: URIs (LOG 455), so nothing is inlined and no module-preload polyfill is injected.
// `npm run dev` serves the sources and passes the API's routes to FF_API (by default the local API's address).
const api = process.env.FF_API ?? "http://127.0.0.1:8480";

export default defineConfig({
  base: "/console/",
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsDir: "assets",
    assetsInlineLimit: 0,
    modulePreload: { polyfill: false },
  },
  server: {
    proxy: Object.fromEntries(["/runs", "/attention", "/me", "/events", "/templates"].map(p => [p, api])),
  },
});
