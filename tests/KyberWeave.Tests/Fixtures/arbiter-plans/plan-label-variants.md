---
id: fixtures/arbiter-plans/plan-label-variants
title: Label family variants
doc-type: plan
status: draft
---

# Label family variants

## implementation tasks

### T1: bold files label with bullets
**Required skill:** `test-dev`
**Files:**
- `src/KyberWeave.Core/Arbiter/Plans/PlanDocument.cs`
- `src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs`

**Depends on:** none.

### T2: files-symbols variant with bare-name inheritance
- **Files / symbols:** `tests/KyberWeave.Tests/Alpha.cs`, plus `Beta.cs` beside it.
- **Depends-on:** T1.

### T3: plain owned variant with path rule rejections
Files/symbols owned: `docs/plans/`, `<env>/settings.yml`, `PlanDocument` and `--force` are not paths.

### T4: scope inside bold, files owned, skills family
- **Scope:** `templates/<component>/README.md`
- **Files owned:** `products/`
- **Skills:** `csharp-dev`, `docs-dev`. Plain text mentions test-dev unbackticked.
- depends-on: T1, T2.

### T5: required skills plural and heading suffix (github-cli)
**Files:** `README.md` only.
**Required skills:** `github-cli`, `github-devops`.

### T6: lowercase label forms
- scope: `products/README.md`
- skills: `test-dev`
