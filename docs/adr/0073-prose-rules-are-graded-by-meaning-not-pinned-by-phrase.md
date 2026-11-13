---
id: adr/0073-prose-rules-are-graded-by-meaning-not-pinned-by-phrase
title: Prose rules are graded by meaning, not pinned by phrase
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-12
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - SystemOneClient
  - StateBudget
---

# ADR 0073: Prose rules are graded by meaning, not pinned by phrase

## Status

Accepted, 2026-11-12.

## Context

The repository ships agent instruction prose: agents, skills, the worker baseline, overlays,
references and governed docs. About 33 test classes pinned that prose deterministically: some 820
exact-phrase assertions (many hidden in helpers and sentence tables), about 85 forbidden phrases,
and SHA-256 body hashes of 18 agent bodies recorded in `products/kyber-squad/migration/`. Any
rewording broke them. One rule change in a week broke 3 tests, made 7 files share one phrase and
forced several hashes to be recomputed, and none of those tests could say whether the rule was
still stated. The migration hashes were read by nothing but a test.

Anthropic's evaluation guidance is to grade the outcome, not the path: deterministic graders where
the property is structural, model graders with a rubric for open-ended text, constrained answers,
and graders calibrated before they are trusted. Jev, TypeSafe's typed decision model, already
answers the Arbiter's questions through `SystemOneClient`
([the Jev decision model](../kyber-arbiter/jev-decision-model.md)).

## Decision

1. **Structure and protocol tokens stay deterministic.** File existence, frontmatter, rosters,
   `delegates-to`, reference resolution, baseline injection, single-home markers, word budgets, and
   the short tokens code parses or emits are asserted exactly, against a code constant where one exists.
2. **Meaning is graded by Jev against one rule catalog.**
   `tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml` lists each rule once: a one-sentence
   statement, the files that must state it, negative controls, and a retired opposite where an old
   rule must not return. The worker rule map's ids are catalog ids, so there is one rule list.
   `ProseRuleCatalogTests` checks the catalog offline on every build; `ProseRuleGradingTests` asks one
   two-option question per rule and document at a 0.8 confidence floor, when `KW_PROSE_GRADE=1` and a key
   resolve. CI passes the `TYPESAFE_API_KEY` secret; without it the test skips.
3. **No phrase, forbidden-phrase or hash pins on prose.** They were removed where the catalog covers
   their rule. The migration reports and their hash tests are deleted. `SquadAgent.BodyDigest`, which the
   renderer and degradation records use at runtime, stays, with its tests.
4. **A rule the grader cannot judge reliably keeps one anchor, not a sentence.** A home that stays below
   the floor after its question is reworded carries an `anchor`, a protocol token or heading it already
   has, checked offline. Every miss and every anchor is visible in the catalog and the report.
5. **Outages do not turn the build red.** A provider error or timeout skips the graded test with a
   warning; a confident wrong answer, an answer below the floor, or a refused key fails it.

## Consequences

- Rewording a rule no longer breaks a test unless the rewording changes what the rule says.
- Deleting a rule's home, heading or catalog entry fails offline; deleting the rule's sentence fails the
  graded run.
- The graded run costs about $0.13, 3 million input tokens and 30 seconds for some 1,100 questions. It
  runs only where the secret exists, so a fork's pull request is checked structurally only.
- Calibration: across the first full run, 9% of answers fell below the floor and none was a confident
  error the text did not justify; questions were rewritten, weak controls replaced and 46 homes
  anchored until three consecutive runs passed. A rule near the floor can still flake; the remedy is a
  better statement or an anchor, never a lower floor.
- Pins on rules outside the catalog, or on files a rule does not name, stay until a rule covers them.

## Related

- [Test coding standard: prose rules](../standards/test/README.md#prose-rules)
- [Worker rule map](../kyber-squad/worker-rule-map.md)
- [ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)
