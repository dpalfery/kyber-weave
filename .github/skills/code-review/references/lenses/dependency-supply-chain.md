# Lens: dependency-supply-chain

**Runner: `review-triage`.** This is a triage lens — its input is the manifest and lock-file diff, and the work is attributing that output to the change rather than judging code. It runs on the fast model profile. If you are `review-lens`, you were misrouted; say so rather than reviewing the code yourself.

## Applicability

Applies when the diff changes a manifest or lock file, adds or upgrades a dependency, changes
a container base image, or changes how build-time artifacts are fetched.

Skip when no dependency, lock file, or build input changed.

## What this lens owns

What this change asks the project to trust that it did not trust before.

## What to look for

**A new dependency with no stated justification.** Every added dependency is a permanent
maintenance obligation and a new party with code execution in the build and often at runtime.
The reviewable question is not "is this a good library" but "was the cost of adding it
weighed, and is that reasoning recorded". Where the repository states a policy on new
dependencies, hold the change to it exactly.

**A dependency added for very little.** A package pulled in for one small function, or one
that duplicates something already present in the tree or the standard library. Check what the
project already depends on before reporting — and check what the new package itself drags in.

**Unpinned or loosened versions.** A range where a pin was, a lock file changed without the
manifest, a lock file deleted or regenerated wholesale in a change that is nominally about
something else. A wholesale lock regeneration hides every individual upgrade inside it.

**Advisories on what is being added or moved to.** Check the specific version this change
lands on, not the package in general.

**Transitive expansion.** A single added direct dependency that brings a large subtree.
Report the count and anything notable in it.

**Provenance.** A dependency fetched from somewhere other than the project's normal registry,
by URL, by git reference, from a fork, or by a name closely resembling a well-known package.
The last is a deliberate attack pattern and is a `critical` finding.

**Build inputs.** A changed base image, a script fetched and executed at build time, a
mutable tag where a digest was pinned.

## What this lens must not report

- Routine upgrades of existing dependencies with no advisory and no behavioural change.
- Outdated dependencies in general — that is managed separately and is explicitly excluded
  from security findings.
- Preferences between comparable libraries.
- Licence compatibility, unless the repository declares a policy this change breaks.
- Dependencies used only by tests, unless the concern is provenance.
