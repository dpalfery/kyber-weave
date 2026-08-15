"use strict";

/**
 * Map Node's process.platform / process.arch to a .NET RID and Release asset names.
 * Supported RIDs must stay in sync with .github/workflows/release.yml.
 */

const RID_MATRIX = {
  "linux-x64": { platform: "linux", arch: "x64" },
  "linux-arm64": { platform: "linux", arch: "arm64" },
  "osx-x64": { platform: "darwin", arch: "x64" },
  "osx-arm64": { platform: "darwin", arch: "arm64" },
  "win-x64": { platform: "win32", arch: "x64" },
};

function resolveRid(platform = process.platform, arch = process.arch) {
  if (platform === "linux" && arch === "x64") {
    return "linux-x64";
  }
  if (platform === "linux" && (arch === "arm64" || arch === "aarch64")) {
    return "linux-arm64";
  }
  if (platform === "darwin" && arch === "x64") {
    return "osx-x64";
  }
  if (platform === "darwin" && arch === "arm64") {
    return "osx-arm64";
  }
  if (platform === "win32" && arch === "x64") {
    return "win-x64";
  }
  return null;
}

function isWindowsRid(rid) {
  return rid === "win-x64";
}

function binaryFileName(tool, rid) {
  const base = tool === "mcp" ? "kyber-weave-mcp" : "kyber-weave";
  return isWindowsRid(rid) ? `${base}.exe` : base;
}

function assetArchiveName(tool, rid) {
  const base = tool === "mcp" ? "kyber-weave-mcp" : "kyber-weave";
  const ext = isWindowsRid(rid) ? "zip" : "tar.gz";
  return `${base}-${rid}.${ext}`;
}

function releaseTagFromVersion(version) {
  const v = String(version || "").replace(/^v/i, "");
  return `v${v}`;
}

module.exports = {
  RID_MATRIX,
  resolveRid,
  isWindowsRid,
  binaryFileName,
  assetArchiveName,
  releaseTagFromVersion,
};
