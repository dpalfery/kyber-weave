---
id: specs/squad-model-presets/requirements
title: Squad model presets requirements
doc-type: requirements
status: draft
owner: dpalfery
last-reviewed: 2026-10-24
component: KyberSquad
keywords:
  - model presets
  - model profiles
  - provider switching
  - inference plan
  - squad preset
---

# Requirements Document

**Phase status:** Draft

## Introduction

Every canonical agent names a model category in its frontmatter (`model-profile: fast`), and
the bundled [`models.yml`](../../../products/kyber-squad/profiles/models.yml) maps each
category to one model per harness. That map ships inside the release, so switching provider
or inference plan today means waiting for a release or hand-editing deployed files, which
Squad's receipt then reports as local edits.

A **model preset** is a named, user-owned override of that map. Squad applies it while it
renders, inside the normal install and update transaction, so deployed files stay
receipt-owned. "Preset" is used because "model profile" already names the category.

Presets and project selections live in the user's home, never in a project's tree, for the
reason [ADR 0037](../../adr/0037-squad-state-in-user-home.md) moved Squad state there.

Slice 1 is Requirements 1–6. Slice 2 is Requirement 7.

## Requirements

### Requirement 1 — Define presets

**User Story:** As a developer, I want to define named presets that map model categories to
models per harness, so that I can keep one per provider or plan.

#### Acceptance Criteria

1.1. Squad SHALL read presets from a file under `~/.kyber-weave/`.
1.2. A preset SHALL use the shape of a `models.yml` profile entry: category → harness →
model token.
1.3. WHEN a preset omits a category or harness THEN Squad SHALL use the bundled
`models.yml` value for it.

### Requirement 2 — Apply a preset

**User Story:** As a developer, I want one command to switch the models of my deployed
agents, so that I don't edit frontmatter by hand.

#### Acceptance Criteria

2.1. WHEN the user runs `kyber-weave squad preset use <name>` with `--target` and optional
`--global` THEN Squad SHALL re-render the selected targets with the preset through the
same transaction `squad update` uses.
2.2. Squad SHALL NOT modify a deployed file outside that transaction.
2.3. A preset SHALL change only the rendered `model` value; tools, permissions and bodies
SHALL be byte-identical to a render without it.
2.4. WHEN `--dry-run` is passed THEN Squad SHALL list each agent whose model would change,
old and new, and write nothing.

### Requirement 3 — Keep the preset across updates

**User Story:** As a developer, I want my chosen preset to survive updates, so that a new
release does not silently reset my models.

#### Acceptance Criteria

3.1. Squad SHALL record the active preset per scope and target in Squad state.
3.2. WHEN `squad update` runs, including to a new release, THEN Squad SHALL re-apply the
recorded preset.
3.3. WHEN the user clears the preset THEN Squad SHALL re-render with the bundled models.
3.4. WHEN a recorded preset no longer exists THEN `squad update` SHALL fail before writing
and name the missing preset.

### Requirement 4 — Fail loudly on bad input

**User Story:** As a developer, I want typos rejected, so that an agent never runs on a
model I didn't choose.

#### Acceptance Criteria

4.1. IF the preset name, a category or a harness is unknown THEN Squad SHALL exit non-zero
before writing and name the offending key.
4.2. IF a preset file does not parse THEN Squad SHALL exit non-zero and name the file and
line.
4.3. An explicit `inherit` value SHALL be accepted and SHALL omit `model`, as the bundled
map does today.

### Requirement 5 — See what is active

**User Story:** As a developer juggling providers, I want to see which preset and models are
in effect, so that I can check before I start work.

#### Acceptance Criteria

5.1. `squad preset list` SHALL show each preset, its source file, and which targets use it.
5.2. `squad preset show <name> --target <harness>` SHALL print the resolved model per
category, marking values that came from the bundled map.
5.3. `squad status` SHALL show the active preset per target, or that none is active.

### Requirement 6 — Project selection

**User Story:** As a developer, I want a project to pick its own preset, so that I don't
pass flags every time.

#### Acceptance Criteria

6.1. Squad SHALL store a project's preset selection under `~/.kyber-weave/`, keyed by the
project's canonical path, as it keys project state today.
6.2. Squad SHALL NOT write a preset or selection file into the project's tree.
6.3. A project selection SHALL NOT change a global install.

### Requirement 7 — Concurrent presets across projects (slice 2)

**User Story:** As a developer, I want two projects on one machine to run the same harness
at the same time on different presets, so that neither needs agents deployed into its
source tree.

#### Acceptance Criteria

7.1. WHEN two projects select different presets for the same harness THEN sessions in both
SHALL be able to run at the same time, each with its agents on its own preset.
7.2. Squad SHALL NOT write agent, skill or preset files into either project's tree to
achieve 7.1.
7.3. Each session SHALL keep the user's sign-in, settings and history for that harness.
7.4. The mechanism SHALL be harness-neutral: one Squad command per project, whatever the
harness.
7.5. IF a harness has no way to meet 7.1–7.3 THEN Squad SHALL say so, name the harness,
and SHALL NOT fall back to a project deploy.

## Success criteria

- Switching every global Claude agent to another provider is one command, followed by no
  `--replace-managed`.
- After `squad update` to a new release, `squad status` still shows the same preset and the
  rendered models match it.
- Two projects run the same harness at once on different presets, and `git status` in both
  shows nothing from Squad.

## Open questions

1. Slice 2 mechanism. Recommended for design: render each preset into its own harness home
   under `~/.kyber-weave/` and start the harness with its home-folder variable pointing
   there. Squad already honours that variable for 9 of the 12 agent harnesses (`claude`,
   `codex`, `cursor`, `copilot`, `opencode`, `kilo`, `pi`, `zcode`, `devin`); `antigravity`,
   `factory` and `commandcode` have none. Design must verify per harness that a preset home
   can share sign-in, settings and history with the real one (7.3).
2. How the variable reaches the session: a `kyber-weave` launcher, a shell hook, or both.
