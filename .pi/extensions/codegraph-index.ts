import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

// Builds the CodeGraph index for this checkout at session start; the script does nothing when the
// index exists or codegraph is not installed, and finds the repository root from its own location.
export default function (pi: ExtensionAPI) {
  pi.on("session_start", async () => {
    const script = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "scripts", "codegraph-session-index.sh");
    spawn("bash", [script], { stdio: "ignore", detached: true }).unref();
  });
}
