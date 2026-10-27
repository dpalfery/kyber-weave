#!/usr/bin/env python3
"""Reply to and resolve review threads from an outcomes file (see SKILL.md).

Usage: post_replies.py <comment-outcomes.json> [--repo owner/name] [--apply]
Default is a dry run: it prints exactly what it would post. With --apply it posts one reply per
comment (outcome fixed OR declined, with the worker's one-line reason) and RESOLVES the thread only
when the outcome is 'fixed'. Declined threads are replied to and left open for a human to disagree.
Threads are matched by the review comment id, never by position. A comment id that is not found in
the live thread list, or a thread that is already resolved, is reported and skipped.

Every outcome is validated before anything is posted: a malformed file exits 2 without touching a
single thread, so a bad list can never leave a pull request half-replied.
"""
import argparse
import json
import subprocess
import sys

FOOTER = "_🤖 Addressed by an automated coding agent_"

# Paginated: a pull request with more than 100 review threads is ordinary on a
# large change, and the first page alone would report every later thread as
# "not found" and silently never reply to it.
THREADS_QUERY = """
query($owner: String!, $name: String!, $pr: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $pr) {
      reviewThreads(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          id
          isResolved
          comments(first: 1) { nodes { databaseId } }
        }
      }
    }
  }
}
"""


def fail(message):
    print(f"post_replies: {message}", file=sys.stderr)
    raise SystemExit(2)


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Reply to and resolve review threads from an outcomes file.",
    )
    parser.add_argument("outcomes", help="path to the comment-outcomes JSON file")
    parser.add_argument("--repo", help="owner/name; defaults to the current checkout")
    parser.add_argument("--apply", action="store_true", help="post and resolve; default is a dry run")
    return parser.parse_args(argv)


def require_int(value, where):
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"{where} must be a positive integer")
    return value


def load_outcomes(path):
    try:
        with open(path, encoding="utf-8") as handle:
            outcomes = json.load(handle)
    except OSError as error:
        fail(f"cannot read {path}: {error}")
    except json.JSONDecodeError as error:
        fail(f"{path} is not valid JSON: {error}")
    if not isinstance(outcomes, list):
        fail(f"{path} must hold a JSON array of outcomes")

    for index, outcome in enumerate(outcomes):
        where = f"{path}[{index}]"
        if not isinstance(outcome, dict):
            fail(f"{where} must be an object")
        for key in ("pr", "comment_id", "outcome", "text"):
            if key not in outcome:
                fail(f"{where} is missing '{key}'")
        require_int(outcome["pr"], f"{where}.pr")
        require_int(outcome["comment_id"], f"{where}.comment_id")
        if outcome["outcome"] not in ("fixed", "declined"):
            fail(f"{where}.outcome must be 'fixed' or 'declined'")
        if not isinstance(outcome["text"], str) or not outcome["text"].strip():
            fail(f"{where}.text must be a non-empty string")
        commit = outcome.get("commit") or ""
        if not isinstance(commit, str):
            fail(f"{where}.commit must be a string")
        if outcome["outcome"] == "fixed" and not commit.strip():
            fail(f"{where}.commit is required for a 'fixed' outcome")
        outcome["commit"] = commit
    return outcomes


def resolve_repo(given):
    repo = (given or "").strip()
    if not repo:
        repo = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"],
            capture_output=True,
            text=True,
        ).stdout.strip()
    if repo.count("/") != 1 or not all(part.strip() for part in repo.split("/")):
        fail(f"--repo must be owner/name, got '{repo}'")
    owner, name = (part.strip() for part in repo.split("/"))
    return repo, owner, name


def gh(*args):
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip()[:300])
    return result.stdout


def live_threads(owner, name, pr):
    """Every review thread on the pull request, keyed by its first comment id."""
    threads = {}
    cursor = None
    while True:
        # owner/name/pr travel as GraphQL variables, never interpolated into the
        # query text: a quote in --repo would otherwise break the document.
        args = [
            "api", "graphql",
            "-f", f"query={THREADS_QUERY}",
            "-f", f"owner={owner}",
            "-f", f"name={name}",
            "-F", f"pr={pr}",
        ]
        if cursor:
            args += ["-f", f"cursor={cursor}"]
        try:
            out = json.loads(gh(*args))
        except (RuntimeError, json.JSONDecodeError) as error:
            print(f"WARN  #{pr}: cannot read threads ({error})")
            return {}
        connection = out["data"]["repository"]["pullRequest"]["reviewThreads"]
        for node in connection["nodes"]:
            comments = node["comments"]["nodes"]
            if comments:
                threads[comments[0]["databaseId"]] = node
        page = connection["pageInfo"]
        if not page["hasNextPage"]:
            return threads
        cursor = page.get("endCursor")
        if not cursor:
            print(f"WARN  #{pr}: thread page claims another page but carries no cursor; stopping")
            return threads


def main(argv):
    settings = parse_args(argv)
    outcomes = load_outcomes(settings.outcomes)
    repo, owner, name = resolve_repo(settings.repo)

    cache = {}
    done = skipped = 0
    for outcome in outcomes:
        pr, cid = outcome["pr"], outcome["comment_id"]
        text = " ".join(outcome["text"].split())[:300]
        threads = cache.setdefault(pr, live_threads(owner, name, pr))
        thread = threads.get(cid)
        if thread is None:
            print(f"SKIP  #{pr} {cid}: thread not found")
            skipped += 1
            continue
        if thread["isResolved"]:
            print(f"SKIP  #{pr} {cid}: already resolved")
            skipped += 1
            continue
        sha = outcome["commit"].strip()
        link = f"[`{sha[:9]}`](https://github.com/{repo}/commit/{sha})" if sha else ""
        lead = f"Fixed in {link}: " if (outcome["outcome"] == "fixed" and sha) else (
            "Not changed: " if outcome["outcome"] != "fixed" else ""
        )
        body = f"{lead}{text}\n\n{FOOTER}"
        resolve = outcome["outcome"] == "fixed"
        print(
            f"{'POST ' if settings.apply else 'DRY  '} #{pr} {cid} "
            f"[{outcome['outcome']}] resolve={resolve}: {text[:110]}"
        )
        if settings.apply:
            gh("api", f"repos/{repo}/pulls/{pr}/comments/{cid}/replies", "-f", f"body={body}")
            if resolve:
                gh(
                    "api", "graphql",
                    "-f", "query=mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}",
                    "-f", f"id={thread['id']}",
                )
        done += 1

    print(f"{'applied' if settings.apply else 'dry run'}: {done} comments, {skipped} skipped")


if __name__ == "__main__":
    main(sys.argv[1:])
