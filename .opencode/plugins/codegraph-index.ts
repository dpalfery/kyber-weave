import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");

// Builds the CodeGraph index in the background; `kyber-weave doctor --ensure` is the whole job.
function ensureIndex(): void {
  try {
    spawn("kyber-weave", ["doctor", "--ensure", "."], { cwd: root, stdio: "ignore", detached: true })
      .on("error", () => {})
      .unref();
  } catch {
    // Best effort: a missing binary must not stop the host from loading.
  }
}

export default {
  id: "codegraph-index",
  async setup() {
    ensureIndex();
  },
};
