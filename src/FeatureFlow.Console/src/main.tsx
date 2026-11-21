import { render } from "preact";
import "./main.css";

// API data reaches the page only as text: JSX children, never markup, and links only from app/links.ts.
// ConsoleTests.The_console_sources_never_build_markup_or_links_from_data enforces it.
render(<h1>Feature Flow</h1>, document.getElementById("app")!);
