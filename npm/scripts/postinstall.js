"use strict";

/**
 * Downloads self-contained CLI + MCP binaries for this platform from the
 * GitHub Release that matches package.json version (tag vX.Y.Z).
 *
 * Skip with: KYBER_WEAVE_SKIP_DOWNLOAD=1
 * Or point at local binaries: KYBER_WEAVE_BINARY_DIR=/path/to/dir
 */

const { ensureBinaries } = require("../lib/download");

async function main() {
  if (process.env.KYBER_WEAVE_SKIP_DOWNLOAD === "1") {
    process.stderr.write(
      "kyber-weave: skipping binary download (KYBER_WEAVE_SKIP_DOWNLOAD=1)\n"
    );
    return;
  }

  try {
    const result = await ensureBinaries();
    process.stderr.write(
      `kyber-weave: installed ${result.rid} binaries under vendor/${result.rid}/\n`
    );
  } catch (err) {
    // Do not fail npm install hard — first run of the bin wrapper will retry.
    // CI that needs the binary should re-run: npm run install-binaries
    process.stderr.write(
      `kyber-weave: postinstall warning: ${err.message}\n` +
        "kyber-weave: binaries will be fetched on first `kyber-weave` / `kyber-weave-mcp` invocation.\n"
    );
  }
}

main();
