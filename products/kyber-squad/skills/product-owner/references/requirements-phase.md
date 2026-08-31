# Requirements Phase

Author the requirements artifact for one feature specification and return a headless digest.

## Inputs

- Feature identity and idea.
- Optional vision or existing requirements artifact.
- On revision, the current artifact and only the user's conductor-relayed feedback.

Resolve the artifact path from **<specification-index>**. Its portable identity is `<active-spec-directory>/{feature-name}/requirements.md`.

## Work

1. Ground references to existing behavior, entities, and boundaries in governed documentation and current code. Delegate external facts to `research-agent` when available.
2. Produce a complete initial requirements set without sequentially prompting the user. Put genuine ambiguity in `OPEN_QUESTIONS`.
3. Persist `Phase status: Draft` until the conductor supplies explicit phase approval. On revision, update the existing artifact rather than regenerating unrelated content.
4. Account for edge cases, user experience, constraints, failure behavior, and measurable success criteria.

## Required artifact

Use a short introduction followed by numbered requirements. Each requirement has one user story and granular acceptance criteria in EARS form:

```markdown
# Requirements Document

**Phase status:** Draft

## Introduction

## Requirements

### Requirement 1
**User Story:** As a [role], I want [capability], so that [benefit].

#### Acceptance Criteria
1.1. WHEN [event] THEN [system] SHALL [response].
1.2. IF [precondition] THEN [system] SHALL [response].
```

Number requirements and criteria so design and tasks can trace them.

## Digest

Return:

```text
STATUS: READY_FOR_REVIEW
PHASE: requirements
ARTIFACT: <resolved requirements path>
SUMMARY: <2-4 sentences>
GAPS: none
OPEN_QUESTIONS: <genuine ambiguities, or none>
```

If the conductor supplies explicit approval, record `Phase status: Approved` and its trace, then return `STATUS: PHASE_APPROVED` with the same phase and artifact fields. Never ask for approval yourself.
