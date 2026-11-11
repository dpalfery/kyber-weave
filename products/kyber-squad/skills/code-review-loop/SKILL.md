---
name: code-review-loop
description: "Complete an authorized pull or merge request review loop: fix findings, validate and push changes, link fix commits in replies, resolve addressed threads, and request another review. Use for end-to-end review remediation or repeated review until clean, across review platforms."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Code Review Loop

Follow [code-review-loop.md](code-review-loop.md) for the workflow. Preserve the user's
chosen PR, reviewer, and authorization scope. Do not resolve a finding until its fix
is verified on the PR branch and its reply links to the pushed commit.

Before fixing a bot review comment, call `arbiter_classify_bot_review_comment` with its path, line,
body and commit. A `real` answer is fixed. A `nitpick` or `already-fixed` answer skips the fix:
reply with the reason and resolve the thread. An unsure answer is `real`.
