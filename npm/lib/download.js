"use strict";

const fs = require("fs");
const path = require("path");
const https = require("https");
const http = require("http");
const { execFileSync } = require("child_process");
const {
  resolveRid,
  isWindowsRid,
  binaryFileName,
  assetArchiveName,
  releaseTagFromVersion,
} = require("./platform");

const OWNER = "dpalfery";
const REPO = "kyber-weave";
const RELEASE_BASE = `https://github.com/${OWNER}/${REPO}/releases/download`;

function packageRoot() {
  return path.join(__dirname, "..");
}

function vendorDir(rid) {
  return path.join(packageRoot(), "vendor", rid);
}

function binaryPath(tool, rid) {
  return path.join(vendorDir(rid), binaryFileName(tool, rid));
}

function readPackageVersion() {
  const pkg = JSON.parse(
    fs.readFileSync(path.join(packageRoot(), "package.json"), "utf8")
  );
  return pkg.version;
}

function ensureDir(dir) {
  fs.mkdirSync(dir, { recursive: true });
}

function downloadToFile(url, destPath) {
  return new Promise((resolve, reject) => {
    const client = url.startsWith("https:") ? https : http;
    const request = client.get(url, { headers: { "User-Agent": "kyber-weave-npm" } }, (res) => {
      if (
        res.statusCode >= 300 &&
        res.statusCode < 400 &&
        res.headers.location
      ) {
        res.resume();
        downloadToFile(res.headers.location, destPath).then(resolve, reject);
        return;
      }
      if (res.statusCode !== 200) {
        res.resume();
        reject(
          new Error(
            `Download failed (${res.statusCode}) for ${url}. ` +
              `Publish a GitHub Release with this asset, or set KYBER_WEAVE_BINARY_DIR.`
          )
        );
        return;
      }
      const out = fs.createWriteStream(destPath);
      res.pipe(out);
      out.on("finish", () => out.close(() => resolve()));
      out.on("error", reject);
    });
    request.on("error", reject);
  });
}

function extractArchive(archivePath, destDir, rid) {
  ensureDir(destDir);
  if (isWindowsRid(rid)) {
    // Prefer PowerShell Expand-Archive on Windows; unzip elsewhere if present.
    if (process.platform === "win32") {
      execFileSync(
        "powershell.exe",
        [
          "-NoProfile",
          "-Command",
          `Expand-Archive -LiteralPath '${archivePath.replace(/'/g, "''")}' -DestinationPath '${destDir.replace(/'/g, "''")}' -Force`,
        ],
        { stdio: "inherit" }
      );
      return;
    }
    execFileSync("unzip", ["-o", archivePath, "-d", destDir], {
      stdio: "inherit",
    });
    return;
  }
  execFileSync("tar", ["-xzf", archivePath, "-C", destDir], {
    stdio: "inherit",
  });
}

async function installToolBinary(tool, rid, version) {
  const tag = releaseTagFromVersion(version);
  const archive = assetArchiveName(tool, rid);
  const url = `${RELEASE_BASE}/${tag}/${archive}`;
  const dest = vendorDir(rid);
  const target = binaryPath(tool, rid);

  if (fs.existsSync(target)) {
    return target;
  }

  ensureDir(dest);
  const tmp = path.join(dest, archive);
  process.stderr.write(`kyber-weave: downloading ${archive} (${tag})…\n`);
  await downloadToFile(url, tmp);
  try {
    extractArchive(tmp, dest, rid);
  } finally {
    try {
      fs.unlinkSync(tmp);
    } catch {
      // ignore cleanup errors
    }
  }

  if (!fs.existsSync(target)) {
    throw new Error(
      `Extracted archive ${archive} but did not find ${path.basename(target)} under ${dest}`
    );
  }

  if (!isWindowsRid(rid)) {
    fs.chmodSync(target, 0o755);
  }

  return target;
}

async function ensureBinaries(options = {}) {
  const overrideDir = process.env.KYBER_WEAVE_BINARY_DIR;
  if (overrideDir) {
    return {
      rid: "override",
      cli: path.join(
        overrideDir,
        process.platform === "win32" ? "kyber-weave.exe" : "kyber-weave"
      ),
      mcp: path.join(
        overrideDir,
        process.platform === "win32"
          ? "kyber-weave-mcp.exe"
          : "kyber-weave-mcp"
      ),
    };
  }

  const rid = resolveRid();
  if (!rid) {
    throw new Error(
      `Unsupported platform ${process.platform}/${process.arch}. ` +
        `Supported: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64. ` +
        `Download a Release asset from https://github.com/${OWNER}/${REPO}/releases`
    );
  }

  const version = options.version || readPackageVersion();
  const cli = await installToolBinary("cli", rid, version);
  const mcp = await installToolBinary("mcp", rid, version);
  return { rid, cli, mcp };
}

function resolveInstalledBinary(tool) {
  const overrideDir = process.env.KYBER_WEAVE_BINARY_DIR;
  if (overrideDir) {
    const name =
      tool === "mcp"
        ? process.platform === "win32"
          ? "kyber-weave-mcp.exe"
          : "kyber-weave-mcp"
        : process.platform === "win32"
          ? "kyber-weave.exe"
          : "kyber-weave";
    return path.join(overrideDir, name);
  }

  const rid = resolveRid();
  if (!rid) {
    return null;
  }
  return binaryPath(tool, rid);
}

module.exports = {
  ensureBinaries,
  resolveInstalledBinary,
  binaryPath,
  vendorDir,
  readPackageVersion,
};
