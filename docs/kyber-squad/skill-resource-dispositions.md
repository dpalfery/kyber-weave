---
id: squad/skill-resource-dispositions
title: Skill-resource dispositions and policy-line ledger
doc-type: reference
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-28
---

# Skill-resource dispositions and policy-line ledger

This reference is the content-preservation audit for the 66 supplemental files that sit
beside the 23 canonical `SKILL.md` files under `products/kyber-squad/skills/`. It exists so
that, for every one of those 66 resources, a reader can answer three questions: what kind of
content it is, what happens to it, and — where it states a rule a host could reverse — where
that rule now lives. It is a governed `reference` document (D5) and it outlives the plan that
created it,
[`2026-09-28-skill-resource-dispositions.md`](../archive/plans/2026-09-28-skill-resource-dispositions.md).

**D2 — a skill's own directory is a durable home.** Review lenses, MSBuild/CI technique, MAUI
platform technique, Pylance/Python procedure, product-owner phase references, PR provider
files and scripts, second-brain's templates, the setup inventory, and
`setup-dev-environment/agents/openai.yaml` all stay exactly where they are, with a recorded
reason. A resource does not have to be policy to be worth keeping; framework-correctness
facts, security floors, and procedure belong in the skill regardless of what a host decides.

**Classification rule, in brief.** A line is **policy** — a stack, library, naming, layout, or
required-practice choice a host could reasonably reverse — when the matching
`standards/<tech>/README.md` template either already states it (`duplicate`, pointer added) or
lacks it (`migrated`, added to the template). A line is **technique** when it is a
framework-correctness fact, a security floor, or a how-to whose violation is a defect no
matter what the host prefers; it stays in the skill. A line that names a project fact or one
host's taste — a project name, a version, a wrapper class, an agent name, a tuning constant,
"do not add Scalar" — is `superseded`: removed from the skill and recorded here, never added
to a template (D4). Only the dal-dev, csharp-dev, test-dev, and maui-dev references may carry
a `migrated`, `duplicate`, or `superseded` ledger row; a policy-looking line found elsewhere is
`retained` with a reason.

**Delivery.** `squad install` and `squad pack` render every file that some Markdown link chain,
starting at a skill's own `SKILL.md`, actually reaches — a file named only in a code span is
packaged into both archives but never rendered beside its principal. Every resource below is
`rendered` except `skills/setup-dev-environment/agents/openai.yaml`, which is `packaged-only`
Codex skill-UI metadata that no instruction links to, and which stays that way by design (D6).
`skills/second-brain/references/templates.md` is `rendered` because `second-brain/SKILL.md`
links `references/templates.md` across its four template references (D6).

**Records, not guidance.** The Policy-line ledger below quotes verbatim excerpts from the
resource files so that later edits can be checked against them (A5). A `migrated`,
`duplicate`, or `superseded` excerpt is a record of wording that is being removed or replaced,
not an instruction to follow — an agent resolving one of these skills should read the file as
it stands, not this ledger's quotations of what it used to say.

## Resource dispositions

| Resource | Content class | Disposition | Delivery | Destination | Verification |
|---|---|---|---|---|---|
| `skills/code-review/references/azure.md` | review-pointer | pointer-already-#126 | rendered | the declared `<azure-coding-standard>` | Pointer-only since #126 (finding 7); resolves the `<azure-coding-standard>` registry token where the host declares one. |
| `skills/code-review/references/csharp.md` | review-pointer | pointer-already-#126 | rendered | the declared `<csharp-coding-standard>` | Pointer-only since #126; resolves `<csharp-coding-standard>` where declared. |
| `skills/code-review/references/github-actions.md` | review-pointer | pointer-already-#126 | rendered | the declared `<github-actions-coding-standard>` | Pointer-only since #126; resolves `<github-actions-coding-standard>` where declared. |
| `skills/code-review/references/pulumi.md` | review-pointer | pointer-already-#126 | rendered | the declared `<pulumi-coding-standard>` | Pointer-only since #126; resolves `<pulumi-coding-standard>` where declared. |
| `skills/code-review/references/python.md` | review-pointer | pointer-already-#126 | rendered | the declared `<python-coding-standard>` | Pointer-only since #126; resolves `<python-coding-standard>` where declared. |
| `skills/code-review/references/react.md` | review-pointer | pointer-already-#126 | rendered | the declared `<react-coding-standard>` | Pointer-only since #126; resolves `<react-coding-standard>` where declared. |
| `skills/code-review/references/sql.md` | review-pointer | pointer-already-#126 | rendered | the declared `<sql-coding-standard>` | Pointer-only since #126 (finding 7 verified this file directly); resolves `<sql-coding-standard>` where declared. |
| `skills/code-review/references/lenses/authz-tenancy.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria consumed directly by the code-review skill's parallel council; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/blast-radius-revertibility.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/correctness.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/dependency-supply-chain.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/di-composition.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/duplicate-implementation.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/infra-workflow.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/intent-alignment.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/model-placement.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/performance.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/prior-art.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/security.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/static-analysis-triage.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/supportability.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/code-review/references/lenses/test-adequacy.md` | lens-criteria | retain-in-place | rendered | — | Review-lens criteria; not a host-reversible policy (D2). |
| `skills/create-pull-request/providers/azure-devops.md` | provider-guidance | retain-in-place | rendered | — | Rewritten by the archived [provider-aware create-pull-request plan](../archive/plans/2026-09-28-provider-aware-create-pull-request.md); provider facts are sourced and cited in this file (D2). |
| `skills/create-pull-request/providers/github.md` | provider-guidance | retain-in-place | rendered | — | Rewritten by the archived provider-aware create-pull-request plan; provider facts are sourced and cited in this file (D2). |
| `skills/create-pull-request/scripts/github-create-pr.ps1` | script | retain-in-place | rendered | — | Rewritten by the archived provider-aware create-pull-request plan when `create-pull-request-github`'s scripts were relinked here (D2). |
| `skills/create-pull-request/scripts/github-create-pr.sh` | script | retain-in-place | rendered | — | Rewritten by the archived provider-aware create-pull-request plan when `create-pull-request-github`'s scripts were relinked here (D2). |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | policy-bearing | policy-migrated-to-template | rendered | csharp standard | 14 Policy-line ledger rows below: 3 migrated gaps and 8 duplicate lines point to `<csharp-coding-standard>`; 1 superseded host taste ("Do not add Scalar", D4); 2 retained framework-correctness/layering technique (D2). |
| `skills/csharp-dev/references/azure-ai-rag.md` | policy-bearing | policy-migrated-to-template | rendered | csharp standard (Safety) | 11 Policy-line ledger rows below: 1 duplicate (secrets) points to `<csharp-coding-standard>` § Safety; 8 superseded project facts, agent names, wrapper classes and tuning constants (D4); 2 retained config-driven/procedural lines (D2). Its portable Azure OpenAI / AI Search technique — batching, hybrid search plus semantic ranking, chunk overlap, citations, transient retries, de-duplication, structured logging — is untouched. |
| `skills/csharp-dev/references/bff-yarp.md` | policy-bearing | policy-migrated-to-template | rendered | csharp standard (Backend-for-frontend) | 13 Policy-line ledger rows below: 7 migrated BFF rules move to the new `csharp § Backend-for-frontend` section; 1 duplicate points to `<csharp-coding-standard>` § Safety; 3 superseded host facts (project filename, package version, .NET version, D4); 2 retained (D2). |
| `skills/csharp-dev/references/build-commands.md` | technique | retain-in-place | rendered | — | C#/.NET build-command technique; not a host-reversible policy (D2). |
| `skills/csharp-dev/references/file-upload.md` | technique | retain-in-place | rendered | — | File-upload implementation technique; not a host-reversible policy (D2). |
| `skills/csharp-dev/references/opentelemetry.md` | technique | retain-in-place | rendered | — | OpenTelemetry instrumentation technique; not a host-reversible policy (D2). |
| `skills/dal-dev/references/adonet-repository.md` | policy-bearing | policy-migrated-to-template | rendered | data-access-layer standard | 10 duplicate Policy-line ledger rows below point to `<data-access-layer-coding-standard>` (the token is already present at :8-9); no gaps or host facts found. |
| `skills/dal-dev/references/migration-scripts.md` | policy-bearing | policy-migrated-to-template | rendered | sql & data-access-layer standards | 11 Policy-line ledger rows below: 3 migrated gaps move to the new `data-access-layer § Migrations` section; 2 duplicate lines point to `<sql-coding-standard>`; 1 line superseded by D3 (the `Down()` mandate); 5 retained, including the rollback command (annotated per D3) and the FluentMigrator/sql-database-architect handoff procedure (D2). |
| `skills/dal-dev/references/schema-design.md` | policy-bearing | policy-migrated-to-template | rendered | sql standard | 31 Policy-line ledger rows below: 5 migrated gaps and 21 duplicate lines point to `<sql-coding-standard>`; 5 retained as security floor or procedure (the Prime Directive, the agent-boundary section, two hardcoded-credential/injection floors, one index-cost rule of thumb — D2). |
| `skills/github-devops/references/build-performance.md` | technique | retain-in-place | rendered | — | MSBuild/CI build technique; not a host-reversible policy (D2). |
| `skills/github-devops/references/ci-build-diagnostics.md` | technique | retain-in-place | rendered | — | CI diagnostics technique; not a host-reversible policy (D2). |
| `skills/github-devops/references/directory-build-organization.md` | technique | retain-in-place | rendered | — | MSBuild project-organization technique; not a host-reversible policy (D2). |
| `skills/github-devops/references/incremental-build.md` | technique | retain-in-place | rendered | — | MSBuild incremental-build technique; not a host-reversible policy (D2). |
| `skills/github-devops/references/msbuild-anti-patterns.md` | technique | retain-in-place | rendered | — | MSBuild correctness technique; not a host-reversible policy (D2). |
| `skills/github-devops/references/msbuild-modernization.md` | technique | retain-in-place | rendered | — | MSBuild modernization technique; not a host-reversible policy (D2). |
| `skills/maui-dev/references/app-lifecycle.md` | technique | retain-in-place | rendered | — | MAUI platform technique; not a host-reversible policy (D2). |
| `skills/maui-dev/references/collectionview.md` | policy-bearing | policy-migrated-to-template | rendered | maui standard (MVVM) | 4 Policy-line ledger rows below: 2 duplicate (`:25`, `:199`) point to `<maui-coding-standard>` § MVVM; 2 retained framework-correctness pitfalls (D2). |
| `skills/maui-dev/references/data-binding.md` | policy-bearing | policy-migrated-to-template | rendered | maui standard (MVVM) | 2 Policy-line ledger rows below: 1 duplicate (`:11`) points to `<maui-coding-standard>` § MVVM; 1 retained framework-correctness detail (D2). |
| `skills/maui-dev/references/dependency-injection.md` | policy-bearing | policy-migrated-to-template | rendered | maui standard (Dependency injection) | 3 Policy-line ledger rows below: 2 duplicate (`:50`, `:120`) point to `<maui-coding-standard>` § Dependency injection; 1 retained framework-correctness pitfall (D2). |
| `skills/maui-dev/references/environment-doctor.md` | technique | retain-in-place | rendered | — | MAUI environment-diagnostic technique; not a host-reversible policy (D2). |
| `skills/maui-dev/references/safe-area.md` | technique | retain-in-place | rendered | — | MAUI platform technique; not a host-reversible policy (D2). |
| `skills/maui-dev/references/shell-navigation.md` | technique | retain-in-place | rendered | — | MAUI platform technique; not a host-reversible policy (D2). |
| `skills/maui-dev/references/theming.md` | technique | retain-in-place | rendered | — | MAUI platform technique; not a host-reversible policy (D2). |
| `skills/pr-review-fix-comments/providers/azure-devops.md` | provider-guidance | retain-in-place | rendered | — | Provider-specific review-thread workflow; not a host-reversible policy (D2). |
| `skills/pr-review-fix-comments/providers/github.md` | provider-guidance | retain-in-place | rendered | — | Provider-specific review-thread workflow; not a host-reversible policy (D2). |
| `skills/product-owner/references/closeout-phase.md` | procedure | retain-in-place | rendered | — | Spec-closeout phase procedure; not a host-reversible policy (D2). |
| `skills/product-owner/references/design-phase.md` | procedure | retain-in-place | rendered | — | Spec-design phase procedure; not a host-reversible policy (D2). |
| `skills/product-owner/references/requirements-phase.md` | procedure | retain-in-place | rendered | — | Spec-requirements phase procedure; not a host-reversible policy (D2). |
| `skills/product-owner/references/tasks-phase.md` | procedure | retain-in-place | rendered | — | Spec-tasks phase procedure; not a host-reversible policy (D2). |
| `skills/python-dev/references/fact-grounded-coding.md` | procedure | retain-in-place | rendered | — | Microsoft Learn / Pylance verification procedure, the same shape as the dal-dev Prime Directive; not a host-reversible policy (D2). |
| `skills/python-dev/references/pylance-docs.md` | technique | retain-in-place | rendered | — | Pylance tool reference; not a host-reversible policy (D2). |
| `skills/python-dev/references/pylance-refactoring.md` | technique | retain-in-place | rendered | — | Pylance refactoring how-to; not a host-reversible policy (D2). |
| `skills/second-brain/references/templates.md` | template | retain-in-place | rendered | — | Template content is skill-owned (D2). Delivery is `rendered` because `second-brain/SKILL.md` links `references/templates.md` across its four template references (D6). |
| `skills/setup-dev-environment/agents/openai.yaml` | metadata | retain-in-place | packaged-only | — | Codex skill-UI metadata (display name, default prompt, implicit-invocation policy); no instruction references it, so no closure reaches it (D2, D6). |
| `skills/setup-dev-environment/references/inventory.md` | inventory | retain-in-place | rendered | — | Host bootstrap inventory reference; not a host-reversible policy (D2). |
| `skills/test-dev/references/e2e-test-patterns.md` | technique | retain-in-place | rendered | — | Playwright E2E technique; not a host-reversible policy (D2). |
| `skills/test-dev/references/integration-test-patterns.md` | policy-bearing | policy-migrated-to-template | rendered | test standard | 9 Policy-line ledger rows below: 7 duplicate lines point to `<test-coding-standard>`; 2 migrated gaps move into the existing `test § Isolation` section. |
| `skills/test-dev/references/mock-usage-analysis.md` | technique | retain-in-place | rendered | — | Mocking-analysis technique; not a host-reversible policy (D2). |
| `skills/test-dev/references/test-maintainability.md` | technique | retain-in-place | rendered | — | Test-maintainability technique; not a host-reversible policy (D2). |
| `skills/test-dev/references/unit-test-patterns.md` | policy-bearing | policy-migrated-to-template | rendered | test standard | 8 duplicate Policy-line ledger rows below point to `<test-coding-standard>`. |

Sixty-six rows: 11 `policy-migrated-to-template`, 7 `pointer-already-#126`, 48
`retain-in-place`, 0 `superseded-intentionally` (every file with a `superseded` ledger row
below also carries a `migrated` or `duplicate` row, so the derivation table resolves it to
`policy-migrated-to-template`). Delivery: 65 `rendered`, 1 `packaged-only`.

## Policy-line ledger

Line numbers are as of base commit `611862e` prior to the policy migration. `Destination`
is `<technology> § <heading>`; `—` where none applies. `Anchor` is set only where `Destination`
is set. `Reason` is required, and shown, only for `superseded` and `retained` rows.

This table covers every normative line — `use`, `never`, `always`, `must`, `do not`, `prefer`,
`avoid`, a required pattern or tool, or a checklist item — in the eight policy-bearing
references (dal-dev's three, csharp-dev's `aspnetcore-webapi.md` / `bff-yarp.md` /
`azure-ai-rag.md`, and test-dev's `integration-test-patterns.md` / `unit-test-patterns.md`) and
in the three policy-bearing maui-dev references. One hundred sixteen rows: 20 `migrated`, 63
`duplicate`, 13 `superseded`, 20 `retained`.

| Source | Line | Excerpt | Disposition | Destination | Anchor | Reason |
|---|---|---|---|---|---|---|
| `skills/dal-dev/references/adonet-repository.md` | 11 | `Do not use EF Core` | duplicate | data-access-layer § Stack | `Dapper and Entity Framework are out of scope` | — |
| `skills/dal-dev/references/adonet-repository.md` | 18 | `Always inject` | duplicate | data-access-layer § Stack | `creates and opens connections. Never` | — |
| `skills/dal-dev/references/adonet-repository.md` | 59 | `Connection strings come from configuration` | duplicate | data-access-layer § Hard rules | `never a literal, never a committed file` | — |
| `skills/dal-dev/references/adonet-repository.md` | 103 | `Use a transaction for multi-statement work` | duplicate | data-access-layer § Hard rules | `Do not nest transactions` | — |
| `skills/dal-dev/references/adonet-repository.md` | 113 | `factory Singleton` | duplicate | data-access-layer § Hard rules | `Register the factory as Singleton and repositories as Scoped` | — |
| `skills/dal-dev/references/adonet-repository.md` | 60 | `Never a literal, never a committed file` | duplicate | data-access-layer § Hard rules | `never a literal, never a committed file` | — |
| `skills/dal-dev/references/adonet-repository.md` | 74 | `or other multi-statement batch, keep the SQL in a constant` | duplicate | data-access-layer § Hard rules | `String concatenation into SQL is an injection` | — |
| `skills/dal-dev/references/adonet-repository.md` | 75 | `value as a typed parameter. Do not interpolate` | duplicate | data-access-layer § Hard rules | `String concatenation into SQL is an injection` | — |
| `skills/dal-dev/references/adonet-repository.md` | 104 | `paired with a data change. Do not nest transactions` | duplicate | data-access-layer § Hard rules | `Do not nest transactions` | — |
| `skills/dal-dev/references/adonet-repository.md` | 116 | `On failure, log with structured properties` | duplicate | data-access-layer § Hard rules | `Do not swallow database exceptions` | — |
| `skills/dal-dev/references/schema-design.md` | 10 | `Before asserting a best practice, version-specific behavior, or syntax, verify against Microsoft Learn` | retained | — | | Procedure: Microsoft Learn verification workflow (the Prime Directive), not a host-reversible policy (D2). |
| `skills/dal-dev/references/schema-design.md` | 16 | `build T-SQL by concatenating unvalidated input. Use parameterized commands and` | retained | — | | Security floor: SQL-injection prevention is a defect regardless of host preference (D2). |
| `skills/dal-dev/references/schema-design.md` | 17 | `Use SQLCLR or an external process instead` | migrated | sql § Least privilege | `Use SQLCLR or an external process instead of xp_cmdshell` | — |
| `skills/dal-dev/references/schema-design.md` | 18 | `Apply **least privilege**: grant minimum permissions` | duplicate | sql § Least privilege | `get the permissions the application uses, and no more` | — |
| `skills/dal-dev/references/schema-design.md` | 19 | `Prefer **Microsoft Entra ID / Kerberos authentication**` | migrated | sql § Least privilege | `Prefer Microsoft Entra ID or Kerberos authentication over SQL authentication` | — |
| `skills/dal-dev/references/schema-design.md` | 20 | `Never hardcode credentials, connection strings, or secrets` | retained | — | | Security floor: credential hardcoding is a defect regardless of host preference (D2). |
| `skills/dal-dev/references/schema-design.md` | 26 | `Schema-qualify every object reference` | duplicate | sql § T-SQL authoring | `Schema-qualify every object reference` | — |
| `skills/dal-dev/references/schema-design.md` | 27 | `SET NOCOUNT ON;` | duplicate | sql § T-SQL authoring | `is the first statement in a stored-procedure body` | — |
| `skills/dal-dev/references/schema-design.md` | 28 | `in stored procedures, views, or table-valued functions. List columns explicitly.` | duplicate | sql § Queries | `couples the caller to column order and leaks whatever gets added later` | — |
| `skills/dal-dev/references/schema-design.md` | 29 | `Do not prefix user stored procedures with` | duplicate | sql § T-SQL authoring | `User procedures take a` | — |
| `skills/dal-dev/references/schema-design.md` | 30 | `SCOPE_IDENTITY()` | migrated | sql § T-SQL authoring | `Use SCOPE_IDENTITY() rather than @@IDENTITY` | — |
| `skills/dal-dev/references/schema-design.md` | 31 | `Make scripts **idempotent**: use` | duplicate | sql § T-SQL authoring | `so a script can run twice` | — |
| `skills/dal-dev/references/schema-design.md` | 33 | `Write **sargable predicates**: don't wrap functions around columns` | migrated | sql § Queries | `Write sargable predicates: do not wrap functions around columns in WHERE or JOIN` | — |
| `skills/dal-dev/references/schema-design.md` | 39 | `Denormalize only as a documented performance decision` | duplicate | sql § Schema and types | `documented performance decision` | — |
| `skills/dal-dev/references/schema-design.md` | 40 | `Choose the **narrowest correct data type**:` | duplicate | sql § Schema and types | `The narrowest correct type.` | — |
| `skills/dal-dev/references/schema-design.md` | 41 | `for Unicode text. Avoid deprecated` | migrated | sql § Schema and types | `Avoid the deprecated text, ntext, and image types` | — |
| `skills/dal-dev/references/schema-design.md` | 42 | `Every table must have a clustered index` | duplicate | sql § Schema and types | `Every table has a clustered index` | — |
| `skills/dal-dev/references/schema-design.md` | 43 | `Ideal clustered key: **narrow, unique, ever-increasing, immutable, non-nullable, fixed-width**` | duplicate | sql § Schema and types | `narrow, unique, ever-increasing, immutable, non-nullable, and fixed-width` | — |
| `skills/dal-dev/references/schema-design.md` | 44 | `as clustered key (16 bytes, not ever-increasing) unless sequentially generated` | duplicate | sql § Schema and types | `unless its values are generated sequentially` | — |
| `skills/dal-dev/references/schema-design.md` | 45 | `Enforce integrity with constraints` | duplicate | sql § Schema and types | `Integrity is enforced with constraints` | — |
| `skills/dal-dev/references/schema-design.md` | 51 | `Order multi-column index keys: equality/join columns first` | duplicate | sql § Indexes | `equality and join columns first` | — |
| `skills/dal-dev/references/schema-design.md` | 52 | `clause to cover queries with non-key columns. Don't include` | duplicate | sql § Indexes | `rather than widening the key, and never include` | — |
| `skills/dal-dev/references/schema-design.md` | 53 | `Before adding an index, check for overlapping indexes` | duplicate | sql § Indexes | `Check for an existing or overlapping index first` | — |
| `skills/dal-dev/references/schema-design.md` | 54 | `For large tables, build/rebuild with` | duplicate | sql § Indexes | `Build or rebuild large indexes` | — |
| `skills/dal-dev/references/schema-design.md` | 55 | `Avoid over-indexing — every index has write and storage cost` | retained | — | | Technique: a general index-cost rule of thumb, not a host-reversible policy (D2). |
| `skills/dal-dev/references/schema-design.md` | 61 | `Single source of truth` | duplicate | sql § Delivery | `source of truth is an SDK-style SQL database project` | — |
| `skills/dal-dev/references/schema-design.md` | 62 | `artifact. Run SQL code analysis during build` | duplicate | sql § Delivery | `with SQL code analysis on, so the rules above are checked by the build` | — |
| `skills/dal-dev/references/schema-design.md` | 64 | `Deployment is diff-based and idempotent` | duplicate | sql § Delivery | `Deploy with SqlPackage` | — |
| `skills/dal-dev/references/schema-design.md` | 65 | `Before production deployment, generate a change preview` | duplicate | sql § Delivery | `generate a preview with SqlPackage` | — |
| `skills/dal-dev/references/schema-design.md` | 66 | `Pass connection strings via secrets; prefer Entra/managed identity` | duplicate | sql § Delivery | `connect with a managed or Entra identity rather than a password` | — |
| `skills/dal-dev/references/schema-design.md` | 70 | `owns schema design end-to-end: table definitions, data types, constraints` | retained | — | | Procedure: the agent-boundary allocation between sql-database-architect, dal-dev and csharp-dev, not a host-reversible policy (D2). |
| `skills/dal-dev/references/migration-scripts.md` | 18 | `fluentmigrator rollback` | retained | — | | Technique: the rollback command stays as an annotated, host-decision-dependent example; sql keeps its forward-only rule (D3). |
| `skills/dal-dev/references/migration-scripts.md` | 50 | `must be safe to re-run: check for existence before creating` | duplicate | sql § Migrations | `idempotent where the platform allows it` | — |
| `skills/dal-dev/references/migration-scripts.md` | 51 | `Every migration **must** implement` | superseded | data-access-layer § Migrations | `Down() is a host decision` | Superseded by D3: `data-access-layer` states that `Down()` is a host decision, and `sql` keeps its forward-only rule. |
| `skills/dal-dev/references/migration-scripts.md` | 52 | `must match exactly what FluentMigrator applies` | retained | — | | Procedure: agent-boundary correctness between sql-database-architect and dal-dev, not a host-reversible policy (D2). |
| `skills/dal-dev/references/migration-scripts.md` | 58 | `One migration per schema change` | migrated | data-access-layer § Migrations | `One migration per schema change` | — |
| `skills/dal-dev/references/migration-scripts.md` | 59 | `Never modify an already-applied migration` | migrated | data-access-layer § Migrations | `Never edit an applied migration; add a new one instead` | — |
| `skills/dal-dev/references/migration-scripts.md` | 61 | `For non-nullable column additions to existing tables` | migrated | data-access-layer § Migrations | `add a default first, then remove the default in a separate migration` | — |
| `skills/dal-dev/references/migration-scripts.md` | 60 | `for audit timestamp columns, not hardcoded values` | retained | — | | Technique: FluentMigrator convention for audit-timestamp defaults, not a host-reversible policy (D2). |
| `skills/dal-dev/references/migration-scripts.md` | 62 | `Schema-qualify all table references` | duplicate | sql § T-SQL authoring | `Schema-qualify every object reference` | — |
| `skills/dal-dev/references/migration-scripts.md` | 63 | `verify with the SQL database project dacpac that schema state matches expectations` | retained | — | | Procedure: post-migration verification step, not a host-reversible policy (D2). |
| `skills/dal-dev/references/migration-scripts.md` | 71 | `Stop — do not apply the migration` | retained | — | | Procedure: the conflict-resolution escalation sequence, not a host-reversible policy (D2). |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 11 | `for request/response DTOs. The compiler generates` | duplicate | csharp § Types | `Request and response DTOs are` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 33 | `produces strongly-typed return values that are reflected in the OpenAPI schema` | duplicate | csharp § Stack | `TypedResults` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 54 | `Reject invalid input with structured` | duplicate | csharp § Errors | `not a bare string` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 87 | `Do not add Scalar as a second UI` | superseded | — | | One host's taste, not portable policy (D4). |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 118 | `with required properties non-nullable` | duplicate | csharp § Types | `Request and response DTOs are` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 119 | `never return domain entities directly` | duplicate | csharp § Stack | `never return a domain entity or persistence row from an action` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 120 | `returning a collection handle the empty-list case` | migrated | csharp § HTTP pipeline | `A collection endpoint returns 200 with an empty array, not 404` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 121 | `attributes match the actual` | retained | — | | Technique: `ProducesResponseType` matching the actual `TypedResults` return type, a framework-correctness fact (D2). |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 122 | `Cancellation token accepted and forwarded to service/repository calls` | duplicate | csharp § Async | `including controller actions — and pass it through` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 123 | `No business logic in controller — delegate to Application service` | retained | — | | Technique: controller-thinness/layering correctness, not a host-reversible policy (D2). |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 124 | `accessed directly — use action method parameters instead` | migrated | csharp § HTTP pipeline | `Actions do not access HttpContext directly; use action method parameters` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 125 | `not bare strings` | duplicate | csharp § Errors | `not a bare string` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 144 | `Use route constraints` | migrated | csharp § HTTP pipeline | `Use route constraints to prevent wrong-type requests from reaching action logic` | — |
| `skills/csharp-dev/references/aspnetcore-webapi.md` | 150 | `Always accept and forward` | duplicate | csharp § Async | `including controller actions — and pass it through` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 32 | `The browser never sees or stores access tokens` | migrated | csharp § Backend-for-frontend | `The browser never sees or stores access tokens` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 97 | `do not restate the exact scope URIs here, they evolve independently of this skill` | retained | — | | Procedure: already defers to the host's Auth Design registry property rather than stating policy itself (D2). |
| `skills/csharp-dev/references/bff-yarp.md` | 98 | `Azure App Configuration + Key Vault process — never via environment variables` | duplicate | csharp § Safety | `Secrets come from User Secrets locally and Key Vault` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 99 | `Server-side in encrypted cookies (never exposed to browser)` | migrated | csharp § Backend-for-frontend | `The browser never sees or stores access tokens` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 120 | `for React HMR` | retained | — | | Technique: development-only CSP relaxation required for React HMR, not a host-reversible policy (D2). |
| `skills/csharp-dev/references/bff-yarp.md` | 121 | `Pigment CSS is CSP-compliant` | superseded | — | | One host's taste (CSS choice), not portable policy (D4). |
| `skills/csharp-dev/references/bff-yarp.md` | 124 | `headers against an allowlist to prevent injection attacks` | migrated | csharp § Backend-for-frontend | `Validate incoming Host headers against an allowlist to prevent header injection` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 129 | `No wildcard origins` | migrated | csharp § Backend-for-frontend | `CORS uses explicit frontend origins, never a wildcard` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 133 | `Fallback route serves` | migrated | csharp § Backend-for-frontend | `The BFF serves a fallback route for client-side routing` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 138 | `MotorcycleRag.WebUI.BFF.csproj` | superseded | — | | Host project fact (project file name), not portable policy (D4). |
| `skills/csharp-dev/references/bff-yarp.md` | 139 | `Yarp.ReverseProxy` | superseded | — | | Host project facts (package version, .NET version, lines 139-141), not portable policy (D4). |
| `skills/csharp-dev/references/bff-yarp.md` | 178 | `Bypass YARP for direct API calls from the BFF` | migrated | csharp § Backend-for-frontend | `Do not bypass YARP for direct API calls from the BFF` | — |
| `skills/csharp-dev/references/bff-yarp.md` | 182 | `Forward Bearer tokens via YARP transforms` | migrated | csharp § Backend-for-frontend | `Forward Bearer tokens via YARP transforms on every proxied request` | — |
| `skills/csharp-dev/references/azure-ai-rag.md` | 67 | `is the sole index reference — do not hardcode a host-specific name` | retained | — | | Technique: config-driven index naming avoids a hardcoded host fact; portable regardless of host (D2). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 97 | `Create new Azure AI Search indexes without approval` | retained | — | | Procedure: change-approval gate for creating new search indexes, not a host-reversible policy (D2). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 25 | `Do not introduce Semantic Kernel` | superseded | — | | Host project fact (framework choice), not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 38 | `QueryPlannerAgent` | superseded | — | | Host agent-class name, not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 53 | `AzureOpenAIClientWrapper` | superseded | — | | Host wrapper-class name, not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 54 | `AzureSearchClientWrapper` | superseded | — | | Host wrapper-class name, not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 61 | `text-embedding-3-large` | superseded | — | | Host tuning constant (embedding model choice), not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 69 | `HNSW (m=4, efConstruction=400, efSearch=500)` | superseded | — | | Host tuning constants, not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 92 | `Microsoft.SemanticKernel` | superseded | — | | Host project fact (same rule as :25), not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 93 | `Use direct Azure SDK calls outside the persistence wrapper layer` | superseded | — | | Host wrapper-layer names, not portable policy (D4). |
| `skills/csharp-dev/references/azure-ai-rag.md` | 94 | `Hardcode Azure endpoints, keys, or connection strings` | duplicate | csharp § Safety | `Secrets come from User Secrets locally and Key Vault` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 8 | `CRITICAL: Never Mock the Database` | duplicate | test § Layer boundaries | `Never mock the database in integration tests.` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 10 | `Integration tests must hit a real SQL Server instance` | duplicate | test § Layer boundaries | `Never mock the database in integration tests.` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 114 | `Each test must arrange its own data` | duplicate | test § Isolation | `Each test arranges its own data` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 115 | `Tests must be order-independent — don't assume another test ran first` | duplicate | test § Isolation | `Tests run in any order and do not depend on each other` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 116 | `Clean up test data in` | migrated | test § Isolation | `Clean up test data in DisposeAsync or use a transaction-per-test pattern` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 117 | `Use unique identifiers (GUID) for test records` | migrated | test § Isolation | `Use unique identifiers for test records to avoid conflicts with parallel runs` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 123 | `Real SQL Server only` | duplicate | test § Layer boundaries | `Use a real SQL Server instance` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 124 | `or polling helpers with timeout` | duplicate | test § What to assert | `polling helper with a timeout` | — |
| `skills/test-dev/references/integration-test-patterns.md` | 125 | `Regression test for every bug fix` | duplicate | test § What to assert | `regression test whose name encodes the scenario that was broken` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 11 | `NSubstitute (prefer over Moq` | duplicate | test § Stack | `Prefer it over Moq` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 20 | `MethodName_StateUnderTest_ExpectedBehavior` | duplicate | test § Stack | `MethodName_StateUnderTest_ExpectedBehavior` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 51 | `Blank lines between Arrange, Act, Assert sections` | duplicate | test § Stack | `Arrange / Act / Assert with blank-line separation` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 53 | `No test logic shared via inheritance` | duplicate | test § Stack | `no test logic shared via inheritance` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 80 | `Prefer builder classes over inline object creation` | duplicate | test § What to assert | `Prefer builder classes over inline object creation` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 95 | `or polling helpers with timeout` | duplicate | test § What to assert | `polling helper with a timeout` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 98 | `Regression tests for every bug fix` | duplicate | test § What to assert | `regression test whose name encodes the scenario that was broken` | — |
| `skills/test-dev/references/unit-test-patterns.md` | 106 | `mock at I/O boundary, never inside domain logic` | duplicate | test § Stack | `Mock at the I/O boundary, never inside domain logic` | — |
| `skills/maui-dev/references/data-binding.md` | 11 | `faster than reflection-based bindings` | duplicate | maui § MVVM | `to its ViewModel so bindings compile` | — |
| `skills/maui-dev/references/data-binding.md` | 27 | `this re-enables reflection and defeats compile-time checking` | retained | — | | Technique: framework-correctness detail about `x:DataType` scope, distinct from the policy choice already ledgered at :11 (D2). |
| `skills/maui-dev/references/collectionview.md` | 25 | `enables compiled bindings and catches typos at build time` | duplicate | maui § MVVM | `to its ViewModel so bindings compile` | — |
| `skills/maui-dev/references/collectionview.md` | 69 | `Do NOT use both` | retained | — | | Technique: a framework-correctness pitfall (double-execution on tap), not a host-reversible policy (D2). |
| `skills/maui-dev/references/collectionview.md` | 147 | `fires multiple times while the user scrolls through the threshold band` | retained | — | | Technique: a framework-correctness pitfall in incremental loading, not a host-reversible policy (D2). |
| `skills/maui-dev/references/collectionview.md` | 199 | `Compiled bindings skip reflection per-cell` | duplicate | maui § MVVM | `to its ViewModel so bindings compile` | — |
| `skills/maui-dev/references/dependency-injection.md` | 50 | `Never register a ViewModel as Singleton` | duplicate | maui § Dependency injection | `AddTransient<LoginViewModel>` | — |
| `skills/maui-dev/references/dependency-injection.md` | 112 | `to prevent null dependency injection on unexpected platforms` | retained | — | | Technique: a framework-correctness pitfall in conditional platform registration, not a host-reversible policy (D2). |
| `skills/maui-dev/references/dependency-injection.md` | 120 | `retains stale data between navigations` | duplicate | maui § Dependency injection | `AddTransient<LoginViewModel>` | — |

## Open follow-ups

- The Policy-line ledger covers every normative line in the eight policy-bearing references
  and the three policy-bearing maui-dev references — not a representative sample. T4a–c edited
  every source line a ledger row names; no policy line was left behind with two homes.
- T3 created two headings that did not exist previously: `data-access-layer § Migrations` and
  `csharp § Backend-for-frontend`. Every `migrated` row above that targets one of them names
  the exact anchor text T3 writes. `bff-yarp.md:99` restates `:32`'s rule and targets the same
  anchor, so it adds no new anchor for T3 to write.
- `dal-dev/references/schema-design.md:18`, `:19`, `:33`, and `:40` quote their bold
  `**...**` markers literally in the Excerpt or Anchor column, because the line's own markdown
  does not leave 12 contiguous backtick-free characters outside them; the excerpt is still a
  verbatim substring.
- Two lines read as normative but are already pointer/deferral prose rather than a stated
  rule — `bff-yarp.md:97` (defers to the host's Auth Design registry property) and
  `azure-ai-rag.md:67`/`:97` (config-driven index naming, already avoiding a hardcoded host
  fact). All three are `retained` with a D2 reason rather than `duplicate` or `migrated`,
  since there is no template rule they restate and no gap for T3 to fill.
- Outside the eleven policy-bearing files, `collectionview.md:69`/`:147`,
  `data-binding.md:27`, and `dependency-injection.md:112` are the clearest remaining
  "never"/"always"/"do not" lines in the three policy-bearing maui-dev references; each is
  `retained` as a framework-correctness pitfall (D2), distinct from the policy lines already
  ledgered for those files. The surrounding XML/table-driven technique in those three files
  (binding-syntax examples, the Performance Tips and Lifetime Rules tables) is descriptive
  how-to rather than a "never/always/must/do not" rule and is not separately ledgered.
