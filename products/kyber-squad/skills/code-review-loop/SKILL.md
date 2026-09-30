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
