---
id: todo/mistyped-release-tag
title: The v1.0.6-rc.6 tag outranks every real release
doc-type: todo
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# The v1.0.6-rc.6 tag outranks every real release

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

The sixth release candidate of 0.1.6 was tagged `v1.0.6-rc.6` rather than `v0.1.6-rc.6`. The
tag list reads:

```
v0.1.6-rc.7
v1.0.6-rc.6   <- intended v0.1.6-rc.6
v0.1.6-rc.5
```

Nothing rejects it, because both the workflow's SemVer guard and
`GitHubSquadReleaseSource.IsValidReleaseVersion` check the *shape* of a version, not its
relationship to the versions already published. `1.0.6-rc.6` is perfectly well-formed.

The consequence is a wrong answer from every "newest" resolution path, since `1.0.6` sorts
above `0.1.6`:

- `kyber-weave update --release-candidate` takes the first non-draft entry from the Releases
  list (`GitHubReleaseClient.ResolveNewestListed`), which GitHub returns newest-created-first
  — so this is currently masked by creation order, not by version comparison.
- `scripts/install.sh --prerelease` does the same first-match selection over the API
  response.
- Any human or tool sorting the tag list by version — including `git tag --sort=version:refname`
  — picks `v1.0.6-rc.6`.

It also means `kyber-weave update 0.1.6-rc.6` fails with "Does that release exist?", because
that tag genuinely does not.

## What is known

- The release workflow refuses to reuse an existing tag but has no monotonicity check:
  `.github/workflows/release.yml`, the `Derive version` step, validates
  `^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$` and then only tests for collision.
- Neither resolution path compares versions. `ResolveNewestListed` and `install.sh`'s
  `resolve_latest_version` both take the first matching entry in GitHub's response order.
  That makes the current behaviour depend on creation timestamps, which is fragile
  independently of this tag.
- The tag carries real published assets, so deleting it breaks anyone who installed from it.

## What needs deciding

- Whether to delete the tag and its Release outright, or leave it and mark the Release as
  superseded. Deletion is cleaner for resolution but breaks a pinned install; the assets are
  a release candidate, so the blast radius is probably one machine.
- Whether the workflow should gain a monotonicity guard (reject a version that sorts below
  the highest existing tag) or only a same-major guard. A hard monotonicity rule would block
  legitimate patch releases on an older line.
- Whether `ResolveNewestListed` should sort by SemVer rather than trusting GitHub's response
  order. That is a correctness fix regardless of what happens to this tag.

## The code seam

- `.github/workflows/release.yml`, `Derive version` step — where a comparison against
  `git tag --sort=-version:refname | head -1` would go.
- `GitHubReleaseClient.ResolveNewestListed` (`src/KyberWeave.Cli/Update/GitHubReleaseClient.cs`)
  and `resolve_latest_version` in `scripts/install.sh` — the two first-match selections.

## How to verify

- `git tag --sort=-version:refname | head -1` names the genuinely newest release.
- `kyber-weave update --release-candidate` from an older build resolves to the newest 0.1.x
  candidate, and keeps doing so when a release is created out of chronological order.
- The release workflow refuses a version that sorts below the newest existing tag, with a
  message naming both.
