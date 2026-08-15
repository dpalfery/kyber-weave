"use strict";

const fs = require("fs");
const path = require("path");
const https = require("https");
const crypto = require("crypto");
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
const MAX_REDIRECTS = 10;

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

/**
 * Download a URL to a file. Redirects are followed only when the next hop is
 * HTTPS — HTTP (or other schemes) are rejected to prevent downgrade attacks.
 */
function downloadToFile(url, destPath, redirectCount = 0) {
  return new Promise((resolve, reject) => {
    if (!url.startsWith("https:")) {
      reject(
        new Error(
          `Refusing non-HTTPS download URL: ${url}. ` +
            `Release assets must be fetched over HTTPS only.`
        )
      );
      return;
    }
    if (redirectCount > MAX_REDIRECTS) {
      reject(new Error(`Too many redirects downloading ${url}`));
      return;
    }

    const request = https.get(
      url,
      { headers: { "User-Agent": "kyber-weave-npm" } },
      (res) => {
        if (
          res.statusCode >= 300 &&
          res.statusCode < 400 &&
          res.headers.location
        ) {
          res.resume();
          const next = new URL(res.headers.location, url).toString();
          if (!next.startsWith("https:")) {
            reject(
              new Error(
                `Refusing non-HTTPS redirect from ${url} → ${next}. ` +
                  `Release assets must be fetched over HTTPS only.`
              )
            );
            return;
          }
          downloadToFile(next, destPath, redirectCount + 1).then(
            resolve,
            reject
          );
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
      }
    );
    request.on("error", reject);
  });
}

function sha256File(filePath) {
  const hash = crypto.createHash("sha256");
  hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

/**
 * Parse SHA256SUMS.txt (GNU coreutils `sha256sum` format:
 * `<hex>  <filename>` or `<hex> *<filename>`).
 */
function parseSha256Sums(text) {
  const map = new Map();
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) {
      continue;
    }
    const match = /^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$/.exec(trimmed);
    if (!match) {
      continue;
    }
    map.set(path.basename(match[2]), match[1].toLowerCase());
  }
  return map;
}

async function fetchSha256Sums(tag) {
  const url = `${RELEASE_BASE}/${tag}/SHA256SUMS.txt`;
  const tmp = path.join(
    require("os").tmpdir(),
    `kyber-weave-SHA256SUMS-${tag.replace(/[^\w.-]/g, "_")}.txt`
  );
  await downloadToFile(url, tmp);
  try {
    const text = fs.readFileSync(tmp, "utf8");
    const map = parseSha256Sums(text);
    if (map.size === 0) {
      throw new Error(`SHA256SUMS.txt for ${tag} contained no checksums`);
    }
    return map;
  } finally {
    try {
      fs.unlinkSync(tmp);
    } catch {
      // ignore cleanup errors
    }
  }
}

function verifyArchiveSha256(archivePath, archiveName, sums) {
  const expected = sums.get(archiveName);
  if (!expected) {
    throw new Error(
      `SHA256SUMS.txt has no entry for ${archiveName}. ` +
        `Refusing to install an unverified Release asset.`
    );
  }
  const actual = sha256File(archivePath);
  if (actual !== expected) {
    try {
      fs.unlinkSync(archivePath);
    } catch {
      // ignore
    }
    throw new Error(
      `SHA256 mismatch for ${archiveName}: expected ${expected}, got ${actual}`
    );
  }
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

async function installToolBinary(tool, rid, version, sums) {
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
    verifyArchiveSha256(tmp, archive, sums);
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
  const tag = releaseTagFromVersion(version);
  process.stderr.write(`kyber-weave: fetching SHA256SUMS.txt for ${tag}…\n`);
  const sums = await fetchSha256Sums(tag);
  const cli = await installToolBinary("cli", rid, version, sums);
  const mcp = await installToolBinary("mcp", rid, version, sums);
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
  parseSha256Sums,
  verifyArchiveSha256,
  sha256File,
};
