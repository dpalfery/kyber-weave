---
name: doc-corpus-integrity-review
description: "Use when a human requests a statement-by-statement review of governed Markdown documents."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Governed documentation corpus review

Use this skill when the user wants a repository documentation corpus reviewed claim by claim with an approval loop before edits.

## Required scope

- Read the repository root `AGENTS.md` before work.
- Use the documentation root and fixed files declared in its Config Reg.
- Process Markdown documents one at a time.
- Exclude `archive/` and `demo/` beneath the documentation root unless the user explicitly changes the scope.
- Deliver reports in chat. Do not create review reports beneath the documentation root.
- Do not commit, branch, reset, restore, or otherwise rewrite unrelated user changes.

## Required Kyber-Weave workflow

Kyber-Weave is mandatory for this skill.

1. Use the Kyber-Weave documentation explorer before reading or grepping governed documentation.
2. Retrieve the target document by its governed identity or path, preferably with `maxDocs: 1` and enough character budget for the complete file.
3. For each claim, query Kyber-Weave for:
   - whether the claim is in the correct document and location;
   - whether another document is the canonical source;
   - whether the claim is duplicated or conflicts with another document;
   - whether the claim aligns with the indexed code and current repository tree.
4. Use CodeGraph for implementation alignment and blast-radius checks when a claim concerns code behavior, dependencies, symbols, routes, or configuration.
5. If Kyber-Weave is unavailable, stop and troubleshoot with the user. Do not silently replace the required check with local grep or an unlabelled fallback.

## Claim decomposition

Break prose into the smallest independently verifiable statements. Split compound sentences and list items when they contain separate claims. Treat these as distinct claim types:

- factual behavior or implementation claims;
- placement and ownership claims;
- normative rules or requirements;
- canonical-source and navigation claims;
- version, command, route, configuration, and dependency claims.

Do not report claims that are confirmed and need no user decision. Keep internal notes in the conversation context or the repository's declared agent scratchpad, never in `<docs-root>`.

## Approval gate

After reviewing one document, stop and report only findings requiring guidance.

Each finding must include:

1. the document and claim;
2. the evidence and conflict or drift;
3. whether the issue is duplication, placement, conflict, or code misalignment;
4. a concrete recommendation;
5. a numbered decision request.

Do not show a list of approved or unchanged claims unless the user asks for it. Do not edit the document or move to the next document until the user decides every reported finding. Accept approvals, reworded decisions, and explicit requests to defer.

## Applying decisions

For each approved or adjusted finding:

- Make the smallest edit that fixes the canonical-source or behavior mismatch.
- Prefer linking to one established source over repeating a rule in multiple documents.
- When behavior is not yet implemented, add a concise governed TODO only when the user approves it. Use the repository's documentation ontology and a unique `todos/<slug>` id.
- Keep TODOs token-efficient: state the gap, evidence, and next action. Do not turn them into implementation plans unless requested.
- Do not add infrastructure, dependencies, code, or documentation beyond the approved decision.
- If two documents duplicate a topic, first identify which document should own the detailed source of truth, then reduce the other to a concise overview or link.

## Validation and progression

After every substantive edit, run the narrowest available validation immediately before further exploration. Then:

- query Kyber-Weave again for the edited document and any affected canonical relationship;
- check internal links, frontmatter, secrets, and relevant code drift;
- run `kyber-weave docs validate` and `kyber-weave docs drift` when the CLI is available;
- if the CLI is unavailable, report that limitation clearly while still using Kyber-Weave MCP and available workspace checks;
- only after validation, select the next in-scope Markdown document in deterministic path order.

At completion, summarize changed files, approved TODOs, validation performed, and any unavailable validation command. Do not claim the corpus is complete unless every in-scope document was processed.
