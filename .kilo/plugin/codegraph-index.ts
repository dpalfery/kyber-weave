import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// Builds the CodeGraph index for this checkout when Kilo loads the plugin; the script does nothing
// when the index exists or codegraph is not installed, and finds the repo root from its own location.
const script = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "scripts", "codegraph-session-index.sh");

export default {
  id: "codegraph-index",
  server: async () => {
    try {
      spawn("bash", [script], { stdio: "ignore", detached: true })
        .on("error", () => {})
        .unref();
    } catch {
      // Indexing is best-effort: a missing bash must not stop the host from loading.
    }
    return {};
  },
};
