---
id: adr/0003-cross-file-duplication-and-prior-art-lenses
title: Cross-File Duplication Detection and Prior-Art Retrieval in Code Review
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# ADR 0003: Cross-File Duplication Detection and Prior-Art Retrieval in Code Review

## Status

Accepted

## Context

During code review, traditional static analysis and single-file review prompts fail to detect cross-file code duplication, redundant type definitions, and speculative abstractions:
1. **Severities & Tool Limitations**: JetBrains retired `dupfinder`, and standard InspectCode runs at `--severity=WARNING` filtered out over 1,200 redundancy and dead-code suggestions.
2. **Narrow Diff Neighborhoods**: Review lenses inspect diffs locally (surrounding file, callers, immediate base classes) and have no visibility into identical helpers or utilities located across the repository tree.
3. **Evidence Integrity**: Vague "this seems redundant" findings lack falsifiable failure scenarios and are discarded by the adjudicating verdict engine.

## Decision

We introduce a combined mechanical and architectural solution for cross-file duplication and prior-art detection:

1. **Solution-Level Redundancy Promotion**:
   - Promoted 29 InspectCode redundancy inspections from `SUGGESTION` to `WARNING` in `KyberWeave.sln.DotSettings` (including unused members, redundant modifiers, and single-part partial types).
   - InspectCode results feed directly into the `static-analysis-triage` lens.

2. **`prior-art` Review Lens**:
   - Evaluates whether new types, helper functions, or abstractions duplicate existing capabilities in the repository.
   - Leverages CodeGraph retrieval to inspect existing domain services and shared helpers before approving net-new utility code.

3. **`duplicate-implementation` Lens and Deterministic Duplicate Gate**:
   - Implemented `kyber-weave review duplicates .` backed by CodeGraph AST normalized statement hashing.
   - Normalizes method bodies by stripping braces, blank lines, whole-line comments, and signatures, clustering method bodies sharing ≥ 4 normalized statements across files.
   - Outputs machine-readable clusters to `artifacts/duplicates.json` for deterministic citation by the `duplicate-implementation` lens.

## Alternatives Considered

- **Whole-repository AST diffing in LLM prompt**: Rejected because whole-repo prompt injection is cost-prohibitive, exceeds context limits, and is non-deterministic.
- **Subjective "taste" findings on brevity/redundancy**: Rejected because subjective taste claims lack reproducible evidence and are dropped by the deterministic verdict engine.

## Consequences

- Cross-file copy-paste duplication and reinvented utilities are caught deterministically prior to merge.
- Redundancy inspections are promoted to active review gates without cluttering source files with `#pragma` directives.
- Review findings cite exact line clusters and prior-art symbols with verifiable evidence.
