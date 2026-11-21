import { type ComponentChildren, createContext } from "preact";
import { useContext } from "preact/hooks";
import { api, read, type Schema } from "../api/client";
import { type Live, useLive } from "../api/live";

const Attention = createContext<Live<Schema<"AttentionList">>>({ loading: true });

/** One read of GET /attention for the page, which the header's count, Needs me and the children's mark share. */
export function AttentionProvider({ children }: { children: ComponentChildren }) {
  const attention = useLive(["attention"], () => read(api.GET("/attention")));
  return <Attention.Provider value={attention}>{children}</Attention.Provider>;
}

/** The open waits the caller may answer now (GET /attention), read again on every change that touches them. */
export const useAttention = (): Live<Schema<"AttentionList">> => useContext(Attention);
