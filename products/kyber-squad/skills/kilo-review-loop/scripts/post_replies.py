#!/usr/bin/env python3
"""Reply to and resolve review threads from an outcomes file (see SKILL.md).

Usage: post_replies.py <comment-outcomes.json> [--repo owner/name] [--apply]
Default is a dry run: it prints exactly what it would post. With --apply it posts one reply per
comment (outcome fixed OR declined, with the worker's one-line reason) and RESOLVES the thread only
when the outcome is 'fixed'. Declined threads are replied to and left open for a human to disagree.
Threads are matched by the review comment id, never by position. A comment id that is not found in
the live thread list, or a thread that is already resolved, is reported and skipped.
"""
import json, subprocess, sys

REPO = None  # set below from --repo or the current checkout
FOOTER = "_🤖 Addressed by an automated coding agent_"
apply = "--apply" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
if "--repo" in sys.argv:
    REPO = sys.argv[sys.argv.index("--repo") + 1]
    args = [a for a in args if a != REPO]
else:
    REPO = subprocess.run(["gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"],
                          capture_output=True, text=True).stdout.strip()
if not REPO or not args:
    sys.exit("usage: post_replies.py <comment-outcomes.json> [--repo owner/name] [--apply]")
OWNER, NAME = REPO.split("/")
outcomes = json.load(open(args[0]))

def gh(*args, stdin=None):
    r = subprocess.run(["gh", *args], capture_output=True, text=True, input=stdin)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip()[:300])
    return r.stdout

def live_threads(pr):
    q = ('query($n:Int!){repository(owner:"%s",name:"%s"){pullRequest(number:$n){'
         'reviewThreads(first:100){nodes{id isResolved comments(first:1){nodes{databaseId}}}}}}}') % (OWNER, NAME)
    try:
        out = json.loads(gh("api", "graphql", "-f", f"query={q}", "-F", f"n={pr}"))
    except RuntimeError as e:
        print(f"WARN  #{pr}: cannot read threads ({e})")
        return {}
    return {n["comments"]["nodes"][0]["databaseId"]: n
            for n in out["data"]["repository"]["pullRequest"]["reviewThreads"]["nodes"]
            if n["comments"]["nodes"]}

cache = {}
done = skipped = 0
for o in outcomes:
    pr, cid, outcome, text = o["pr"], o["comment_id"], o["outcome"], " ".join(o["text"].split())[:300]
    threads = cache.setdefault(pr, live_threads(pr))
    t = threads.get(cid)
    if t is None:
        print(f"SKIP  #{pr} {cid}: thread not found"); skipped += 1; continue
    if t["isResolved"]:
        print(f"SKIP  #{pr} {cid}: already resolved"); skipped += 1; continue
    sha = (o.get("commit") or "").strip()
    link = f"[`{sha[:9]}`](https://github.com/{REPO}/commit/{sha})" if sha else ""
    lead = f"Fixed in {link}: " if (outcome == "fixed" and sha) else ("Not changed: " if outcome != "fixed" else "")
    body = f"{lead}{text}\n\n{FOOTER}"
    resolve = outcome == "fixed"
    print(f"{'POST ' if apply else 'DRY  '} #{pr} {cid} [{outcome}] resolve={resolve}: {text[:110]}")
    if apply:
        gh("api", f"repos/{REPO}/pulls/{pr}/comments/{cid}/replies", "-f", f"body={body}")
        if resolve:
            gh("api", "graphql",
               "-f", "query=mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}",
               "-f", f"id={t['id']}")
    done += 1
print(f"{'applied' if apply else 'dry run'}: {done} comments, {skipped} skipped")
