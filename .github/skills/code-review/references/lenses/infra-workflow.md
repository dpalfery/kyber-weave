# Lens: infra-workflow

## Applicability

Applies when the diff touches infrastructure definitions, continuous-integration or
deployment workflow files, database migrations, or cloud resource configuration.

Skip when it touches none of these. This lens skips on most changes, and that is the design.

## What this lens owns

The definitions that decide how software is built, deployed, and stored — where a defect
affects every change that follows rather than only this one.

Load the technology checklists for whatever is actually present, from the sibling reference
files in this skill: infrastructure-as-code, cloud resources, workflow definitions, and
database standards. Apply those checklists; this file adds only what is common across them.

## What to look for

**Migrations that are not safe to run.** A migration that locks a large table, rewrites it in
place, drops a column still read by the deployed version, or cannot be applied while the
previous version is serving. Check that it is reversible, or that its irreversibility is
deliberate and stated. A migration and the code depending on it landing in one deployment is
an ordering requirement — check that it holds in both directions.

**Workflow trust boundaries.** A workflow triggered by untrusted input that also has access to
credentials. Untrusted values interpolated into a shell step. A third-party action referenced
by a mutable tag rather than an immutable digest. Secrets exposed to a job that does not need
them, or reachable from a fork.

**Permissions granted by default.** Workflows and infrastructure roles granted more than the
task needs, or inheriting broad defaults where a narrow explicit grant was available.

**State and drift in infrastructure code.** Resources whose identity depends on ordering, a
change that would destroy and recreate rather than update, hardcoded values that belong in
configuration, secrets present in the definition rather than referenced from a store.

**Destructive changes to durable resources.** A modification that would delete or replace
something holding data. This is the highest-consequence finding this lens produces; report it
at `critical` and describe exactly what would be lost.

**Gates that stopped gating.** A required check made optional, a failure condition weakened, a
step allowed to continue on error, a timeout raised to mask a hang. Any of these inside a
change that is nominally about something else deserves particular attention.

## What this lens must not report

- Formatting or naming in configuration files, unless the repository declares a standard.
- Preferences between tools, providers, or runner images.
- Cost optimization, unless the change makes a resource dramatically more expensive by
  accident.
- Application-code findings in a change that also touches infrastructure — other lenses own
  those files.
