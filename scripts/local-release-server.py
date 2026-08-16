#!/usr/bin/env python3
"""A loopback stand-in for the GitHub Releases endpoints kyber-weave reads.

Serves just enough of the API for `kyber-weave update` and `kyber-weave squad install`
to run against locally built artifacts:

    GET /healthz                                    -> readiness probe
    GET /repos/<owner>/<repo>/releases              -> release list
    GET /repos/<owner>/<repo>/releases/latest       -> newest non-prerelease
    GET /repos/<owner>/<repo>/releases/tags/<tag>   -> one release, with assets
    GET /<owner>/<repo>/releases/download/<tag>/<f> -> asset bytes

Releases are discovered from the layout `<root>/<tag>/<asset files>`. The list and
latest endpoints cannot both be plain files on disk (`releases` would have to be a
file and a directory at once), which is why this is a router and not a static server.

Discovery runs per request rather than at startup, so the server can be launched
before — or alongside — whatever is publishing into the tree, and needs no restart
when a new version appears. The scan is two listdir calls; at this scale that costs
less than the ordering constraint it removes.

Binds loopback only. Prints the chosen port on the first line of stdout so a caller
can pass `--port 0` and read back the ephemeral port.
"""

import argparse
import json
import os
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlparse

TAG_PATTERN = re.compile(r"^v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?$")
ASSET_PATTERN = re.compile(r"^[A-Za-z0-9._-]+$")


def discover(root):
    """Maps tag -> {asset name: absolute path}, skipping anything unrecognized."""
    releases = {}
    for entry in sorted(os.listdir(root)):
        directory = os.path.join(root, entry)
        if not os.path.isdir(directory) or not TAG_PATTERN.match(entry):
            continue
        assets = {}
        for name in sorted(os.listdir(directory)):
            path = os.path.join(directory, name)
            if os.path.isfile(path) and ASSET_PATTERN.match(name):
                assets[name] = path
        releases[entry] = assets
    return releases


def sort_key(tag):
    """Orders tags newest-last by (major, minor, patch, release-beats-prerelease)."""
    core = tag[1:].split("-", 1)
    numbers = [int(part) for part in core[0].split(".")]
    return (numbers, 1 if len(core) == 1 else 0, tag)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    # Silences the default per-request stderr line; the harness prints its own progress.
    def log_message(self, fmt, *args):
        if self.server.verbose:
            sys.stderr.write("release-server: " + (fmt % args) + "\n")

    @property
    def releases(self):
        """Rescans the tree per request; see the module docstring."""
        return discover(self.server.root)

    def release_json(self, tag, releases):
        assets = releases[tag]
        origin = f"http://{self.headers.get('Host', self.server.origin)}"
        return {
            "tag_name": tag,
            "name": tag,
            "draft": False,
            "prerelease": "-" in tag,
            "assets": [
                {
                    "name": name,
                    "size": os.path.getsize(path),
                    "browser_download_url":
                        f"{origin}/{self.server.owner}/{self.server.repo}"
                        f"/releases/download/{tag}/{name}",
                }
                for name, path in sorted(assets.items())
            ],
        }

    def send_json(self, payload, status=200):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_bytes(self, path):
        size = os.path.getsize(path)
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(size))
        self.end_headers()
        with open(path, "rb") as handle:
            while True:
                chunk = handle.read(1 << 16)
                if not chunk:
                    break
                self.wfile.write(chunk)

    def fail(self, status, message):
        self.send_json({"message": message}, status)

    def do_GET(self):  # noqa: N802 - BaseHTTPRequestHandler's required spelling
        path = unquote(urlparse(self.path).path)
        owner, repo = self.server.owner, self.server.repo

        # Answered before any filesystem work so a readiness probe stays meaningful
        # even when the release tree is still being written.
        if path == "/healthz":
            self.send_json({"status": "ok"})
            return

        releases = self.releases

        api_prefix = f"/repos/{owner}/{repo}/releases"
        download_prefix = f"/{owner}/{repo}/releases/download/"

        if path == api_prefix:
            ordered = sorted(releases, key=sort_key, reverse=True)
            self.send_json([self.release_json(tag, releases) for tag in ordered])
            return

        if path == f"{api_prefix}/latest":
            stable = [tag for tag in releases if "-" not in tag]
            if not stable:
                self.fail(404, "no stable release")
                return
            self.send_json(self.release_json(max(stable, key=sort_key), releases))
            return

        if path.startswith(f"{api_prefix}/tags/"):
            tag = path[len(f"{api_prefix}/tags/"):]
            if tag not in releases:
                self.fail(404, f"no release {tag}")
                return
            self.send_json(self.release_json(tag, releases))
            return

        if path.startswith(download_prefix):
            remainder = path[len(download_prefix):].split("/")
            if len(remainder) != 2:
                self.fail(404, "malformed asset path")
                return
            tag, name = remainder
            asset = releases.get(tag, {}).get(name)
            if asset is None:
                self.fail(404, f"no asset {name} in {tag}")
                return
            self.send_bytes(asset)
            return

        self.fail(404, f"unrouted path {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, help="directory of <tag>/<assets> trees")
    parser.add_argument("--port", type=int, default=0, help="0 picks an ephemeral port")
    parser.add_argument("--owner", default="dpalfery")
    parser.add_argument("--repo", default="kyber-weave")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    if not os.path.isdir(args.root):
        sys.exit(f"release-server: {args.root} is not a directory")

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    server.root = args.root
    server.owner = args.owner
    server.repo = args.repo
    server.verbose = args.verbose
    server.origin = f"127.0.0.1:{server.server_address[1]}"

    # Emitted before the release listing so a caller polling for the port is unblocked
    # by the earliest possible write.
    print(server.server_address[1], flush=True)

    releases = discover(args.root)
    for tag in sorted(releases, key=sort_key, reverse=True):
        print(f"{tag}: {len(releases[tag])} assets", file=sys.stderr)
    if not releases:
        print(f"no v* release directories under {args.root} yet", file=sys.stderr)
    sys.stderr.flush()

    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
