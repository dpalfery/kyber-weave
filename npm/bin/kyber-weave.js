#!/usr/bin/env node
"use strict";

const { spawnSync } = require("child_process");
const fs = require("fs");
const {
  ensureBinaries,
  resolveInstalledBinary,
} = require("../lib/download");

async function main() {
  let binary = resolveInstalledBinary("cli");
  if (!binary || !fs.existsSync(binary)) {
    const installed = await ensureBinaries();
    binary = installed.cli;
  }

  const result = spawnSync(binary, process.argv.slice(2), {
    stdio: "inherit",
    env: process.env,
  });

  if (result.error) {
    console.error(result.error.message);
    process.exit(1);
  }
  process.exit(result.status === null ? 1 : result.status);
}

main().catch((err) => {
  console.error(err.message || err);
  process.exit(1);
});
