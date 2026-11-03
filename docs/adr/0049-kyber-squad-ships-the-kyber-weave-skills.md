---
id: adr/0049-kyber-squad-ships-the-kyber-weave-skills
title: Kyber-Squad Ships the Kyber-Weave Skills Beside Its Own Skills
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-01
component: KyberSquad
code-refs:
  - SquadCompanionSkills
  - SquadPacker
  - SquadPackCommand
  - LocalCheckoutSquadReleaseSource
decided-by: []
supersedes: []
---

# ADR 0049: Kyber-Squad Ships the Kyber-Weave Skills Beside Its Own Skills

## Status

Accepted, 2026-10-31, with the plan in [the archive](../archive/plans/2026-10-31-kyber-skills-user-level-deploy.md).
It reverses the product boundary recorded in `products/kyber-squad/README.md`. That README
said the three Kyber-Docs skills were "intentionally managed separately" and "not part of
Kyber-Squad". The sentence is removed in place, and this record holds the decision.

## Context

Kyber-Squad deploys its 28 skills from `products/kyber-squad/skills/` into each selected
harness's user-level folder. A global install therefore puts `task-gate` in `~/.claude/skills/`.

Six Kyber-Weave skills live in `.apm/skills/`: `kyber-docs-authoring`,
`kyber-docs-integrity-review`, `kyber-docs-onboarding-and-assessment`, `kyber-weave-cli`,
`kyber-weave-model-profile`, and `spec-conductor`. Until this decision they reached users only
through APM, which `kyber-weave docs init` runs inside one project. It has no global option. A
Claude Code user who installed Kyber-Squad globally never received them at user level.

Four facts constrain the choice:

- The APM package reads `.apm/` (`includes: auto`), and `docs init` deploys from it. Package-shape
  tests pin `targets: [agent-skills]` and forbid a self-dependency (issues #285 and #289).
- Squad's receipt, update, and uninstall are the only lifecycle that keeps user-level files in
  step with a version-locked release (KS-004 and KS-005).
- The Squad loader, the bundle roster, and the renderers were written for one canonical tree of
  28 skills.

## Decision

1. **Kyber-Squad deploys the Kyber-Weave skills with its own skills.** Install, update, status,
   doctor, and uninstall, at global and project scope and on every target, carry the six skills
   beside the 28 Squad skills, under the same receipt. This decision adds no flag, no default,
   and no skip to `kyber-weave docs init`: a change another record makes to that command is that
   record's, not this one's.
2. **`.apm/skills/` stays the only source.** Nothing is moved, copied, or symlinked into
   `products/kyber-squad/`. When Squad builds an archive or a checkout release source, it adds
   every `.apm/skills/<name>/**` file as `skills/<name>/**`. One Core type,
   `SquadCompanionSkills`, does this for both callers, so pack and a checkout run cannot disagree.
3. **Every directory under `.apm/skills/` that holds a `SKILL.md` ships.** Discovery replaces a
   list, so a skill added there ships with the next release.
4. **Both archives carry the skills.** The APM bundle and the Agent Plugins archive each hold 34
   skills (28 Squad, 6 Kyber-Weave) and 125 skill-tree files (34 `SKILL.md` and 91 resources).
   `bundles/full.yml` still lists only the 28 Squad skills.
5. **A name clash fails closed.** A companion skill whose name equals a Squad skill directory or
   a Squad agent file (`agents/<name>.md`), ignoring letter case, makes `squad pack` exit 1 and write
   no archive. The message names both paths. The checkout release source throws before it writes
   anything. Neither side silently wins.
6. **A symbolic link fails closed.** A link at `.apm` or `.apm/skills`, at a companion skill's
   directory, at its `SKILL.md`, or at any file or folder inside a companion skill is refused with an
   error that names the link. Pack exits 1 and writes no archive. The checkout release source throws
   before it writes anything.
7. **Pack validates the composed tree.** `squad pack` loads the real Squad tree first. It then
   packs the composed tree into a scratch folder, extracts it, loads it, and removes the scratch
   folder on every path. A refused pack leaves the previous archive in place. A failed write does
   too: each archive is written to a temporary file beside its target and replaces the target only
   when complete.
8. **Where Squad is deployed, skip the docs-init skill.** A project that has Squad deployed and
   also runs `kyber-weave docs init` should pass `--no-skill`, because Squad already delivers the
   same skills.

## Alternatives considered

1. **Move the six into `products/kyber-squad/skills/`.** Rejected. The APM package reads
   `.apm/`, so the move breaks `apm install` and `docs init`, and the package-shape tests fail.
2. **Mirror the six into `products/kyber-squad/skills/` with a drift test.** Rejected. Two
   sources drift.
3. **Symlink `products/kyber-squad/skills/<name>` to `.apm/skills/<name>`.** Rejected. Links break
   on Windows checkouts and fail the loader's path-discovery guard.
4. **Add `docs init --global`, so APM installs into user-level folders.** Rejected. It adds a
   second mechanism with a runtime APM dependency. It resolves the package from GitHub `main`
   rather than the version-locked Squad release, keeps no receipt, and has no update or uninstall.
   It also adds a public CLI flag.
5. **Deploy per harness into project folders, as Squad does.** Rejected. It writes only project
   folders, so the reported case, user-level folders, stays unfixed.
6. **Ship only the three `kyber-docs-*` skills from an explicit list.** Rejected. Nothing records
   why the other three sit outside Squad, and a second list drifts from the first.
7. **Let one side win a name clash.** Rejected. One skill would vanish from every harness with no
   signal.

## Consequences

- One source. A change to a `.apm/skills` skill reaches the APM package and Squad at the next
  release.
- A skill reaches a user's machine only through a release that carries it, followed by
  `kyber-weave update` or `kyber-weave squad update --global`. A CLI run from a checkout gets the
  skills at once.
- A user who copied a Kyber-Weave skill by hand to a path Squad now owns sees `squad update`
  refuse and write nothing (`UnmanagedCollision`). The remedy is to remove the copy, or to adopt
  it with `--adopt` when its bytes match the release.
- **Version skew (R3).** `docs init` installs from GitHub `main` through APM, while Squad installs
  the version-locked release. One project can hold two versions of a skill, at different levels.
- **Overlap (R2, R5).** Squad at project scope and `docs init` with the default `agent-skills`
  target write the same `.agents/skills/<name>` folder for the Antigravity target. The collision
  fails safe: Squad refuses to overwrite a file it does not own, and `squad status` reports a
  managed file whose bytes changed. A harness that reads both user and project folders may list a
  name twice when Squad is global and `docs init` targets that harness in a project. Impact is low.
- **Discovery (R4).** A skill added to `.apm/skills/` ships with Squad automatically. Composed-tree
  validation at pack, and the install-time loader, check each release.
- **Known gap, not introduced here (D17).** A symbolic link inside `products/kyber-squad/` that
  points outside the repository is still packed when nothing loaded references it. The pack walk
  follows links, and the loader checks only the files it reads. The gap predates this decision and
  is out of scope for it. A follow-up is proposed, and no todo is filed yet.
- **Known gap, introduced here (case).** Two companion skill folders whose names differ only by
  case, such as `Foo` and `foo` on a case-sensitive checkout, are both packed and would collide on a
  case-insensitive install; the fix is deferred as a follow-up.
