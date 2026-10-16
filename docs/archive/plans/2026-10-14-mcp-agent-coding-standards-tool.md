---
id: plans/2026-10-14-mcp-agent-coding-standards-tool
title: "MCP tool for technology coding standards (agent_coding_standards)"
doc-type: plan
status: complete
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-15
development-mode: test-first
---

# MCP tool for technology coding standards (`agent_coding_standards`)

**Status: Complete 2026-10-15.** T1–T7 executed test-first, every task audit PASS, the
whole-change review APPROVED at the finding level (its single engine escalation,
`KW-REVIEW-008` on the `always-human` `.kyber-weave/kyber-weave.yml` path, was signed off by
the user), and the declared gate suite 15/15. The frontmatter stays `status: current` only
because this document still sits in `docs/plans/`: the ontology's status vocabulary
(`current`, `draft`, `needs-review`, `superseded`) has no completion value, and `complete`
is legal only under the archive subtree the corpus does not validate. The archive
transition — the move to `docs/archive/plans/`, the frontmatter flip to `status: complete`,
and the inventory row move described in §7.4 — is the finishing PR's closing step. Closeout
verification is §7.5.
As of 2026-10-14 the plan was decision-complete — the user decisions (DEC-1 to DEC-5) are
recorded verbatim below, and the design-level choices D6–D13 are settled by the architect
under the conductor's planning mandate (D12 and D13 by gate adjudication, below) — and
**Approve-and-execute gate 2026-10-14: APPROVED**, the user answering "Approve and
execute"; that approval is recorded in the Status / approval provenance field of every
Decisions row below. The 2026-10-15 post-merge re-assessment raised D14–D16 and briefly
reopened this plan; **D14 was withdrawn the same day** — git-plumbing verification showed
its premise was a race artifact (the re-assessment read `DocumentIndex.cs` while the T2
worker was writing it; the engine was authored in-session by T2, not delivered by the merge
— see D14's corrected row) — so the reopen is void and this Ready state, exactly as it
stood after the D12/D13 amendments and their user approval, is restored. D15 and D16 were
**independently verified 2026-10-15 — both claims confirmed correct, no corrections** — and
both were settled at the 2026-10-15 conductor gate: the user re-approved the D15-amended
T3/T4 contract cells (verbatim: "Approve: extend the server instructions and add the pinning
assert") and answered D16 — add both `docs_status` and `agent_coding_standards` to the squad
roster, with no parity-guard carve-out (verbatim: "Add both docs_status and
agent_coding_standards to the roster (recommended)"). The plan was decision-complete with no
open items, so T3–T7 executed. **Execution: T1–T7 complete** — T1's RED
was genuine (18 `CS1061` compile errors against the then-absent
`DocumentIndex.CodingStandards` / `CodingStandardTechnologies` — 15 naming `CodingStandards`,
3 naming `CodingStandardTechnologies` — recorded before T2 ran; T1's pass-1 audit found the
padded-`technology` clause untested and closed the gap with a mutation-proven test), and
T2's implementation is green (33/33 in `CodingStandardsLookupTests`, 49/49 across
`DocumentIndexTests.cs`'s four classes, 2831/2831 across the project, format and analyzer
gates clean).
**Execution after the 2026-10-15 gate:** T3 (the tool and packaging tests, authored with the
reflection+Invoke idiom D12 requires), T4 (the `agent_coding_standards` tool plus the
`DocsServerInstructions.Text` roster entry), T5 (the shipped-roster parity guard and the
`products/kyber-squad/toolchain.yml` edit adding both `agent_coding_standards` and
`docs_status`) and T6 (the same-change `docs/docgraph/mcp-runbook.md` documentation) all
completed; T7 ran the declared 15-gate suite and the four-lens whole-change review. The
mutation evidence and durable facts that until now existed only in session scratch are
recorded in §7.5.
Validation: the conductor's worker ran both documentation checks against this plan's Draft
on 2026-10-14 (§7.2) — plain `docs validate .` zero findings, `docs drift .` zero findings
with a usable CodeGraph index. This Ready save itself was not re-validated in the finalizing
session (no process-execution tool available); T6 and T7 re-run both checks against the
changed corpus regardless. The 2026-10-14 D12-approval and D13 cap-removal edits, the 2026-10-15 D14-withdrawal
edits, and the 2026-10-15 D15/D16 verification-and-answer edits were likewise made without
an in-session validation run (no process-execution tool in the harness).

**Amendment 2026-10-14 (D12):** the conductor surfaced a scheduling hazard in the approved
graph — T1 and T3 both add tests to the one test assembly, so under T1 ∥ T3 concurrency
neither task's RED evidence was observable at its own boundary. Adjudicated as a sequencing
defect (the repo's own tests already compile against a not-yet-existing tool via reflection,
§2): T1–T4 now serialize (T1 → T2 → T3 → T4) and T3's tool-response tests are authored with
the reflection+Invoke idiom. The T3 RED-evidence cell and the T2 GREEN-acceptance cell
amended with D12 reopened at the 2026-10-14 conductor gate, where the user approved the
amended contracts — verbatim answer: "Approve amended contracts and resume"; every other
approval (DEC-1 to DEC-5, D6–D11, the approve-and-execute gate) stands.

**Amendment 2026-10-14 (D13):** at the same gate the user rejected any concurrency cap —
verbatim: "no max concurrency every!!! remove max concurrency from everything". §6's former
`MAX_CONCURRENCY: 2` declaration is removed. Dispatch is governed by readiness alone: every
task whose declared dependencies are satisfied is dispatched immediately, with no cap on
parallelism; only the dependency graph (D12's T1 → T2 → T3 → T4 chain included) and each
task's file/symbol scope constrain order. D13 records the decision.

**Amendment 2026-10-15 (post-merge re-assessment, D15–D16; D14 withdrawn):** the checkout
was fast-forwarded `2794cc8..50e5ae4` (28 commits from `origin/main`; `.git/logs/HEAD`
shows a pure fast-forward with no local commits). This pass first recorded D14 — that
merged HEAD already carried `DocumentIndex.CodingStandards`,
`CodingStandardTechnologies`, and the private `WholeExcerpt`, so T1/T2 became
verify/reconcile — and reopened the plan on that premise. **D14 is withdrawn.**
Git-plumbing verification over the same range established the opposite: neither the HEAD
blob nor the index blob of `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` contains
those members, no commit in `2794cc8..50e5ae4` touched the file, and the members existed
only as unstaged working-tree additions (+173 lines, purely additive) written in-session
by the T2 worker. The re-assessment had read the file while that worker was writing it and
mistaken T2's just-written code for merged state — a race artifact, not a merged premise.
T1's RED was genuine (18 `CS1061` errors against the then-absent members, recorded before
T2 ran), T2 was genuine implementation, and the plan was never invalidated; the reopen
and the T1/T2 re-scope are reverted, restoring the D12-amended contract cells (both tasks
are now complete with passing evidence). Two further claims from the same pass were put to independent verification against the real
files and **confirmed correct with no corrections (2026-10-15)**: **D15** — commits 339/342
added a same-change server-instructions surface
(`src/KyberWeave.Mcp/DocsServerInstructions.cs`, pinned by
`McpPackagingTests.ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch`) that the
sixth tool must also extend, amending the T3/T4 cells; and **D16** — the squad roster
survives at `products/kyber-squad/toolchain.yml` (a worker report that it moved to
`bundles/full.yml` is wrong — that file is an agents+skills bundle with no MCP roster)
but is missing `docs_status`, so the approved T5 parity guard also catches that
pre-existing gap. Both were settled at the 2026-10-15 conductor gate: the user re-approved
the D15-amended T3/T4 cells (verbatim: "Approve: extend the server instructions and add the
pinning assert") and answered D16 — add both names, `docs_status` and
`agent_coding_standards`, to the roster with no carve-out (verbatim: "Add both docs_status
and agent_coding_standards to the roster (recommended)") — so T3/T4 may execute and T5 is
unblocked. Every prior approval (DEC-1 to DEC-5, D6–D13, both 2026-10-14 gates) stands
undisturbed. This
amendment pass ran neither `docs validate` nor `docs drift` (no process-execution tool in
the harness); the conductor's worker re-runs both, as every prior edit pass also required.

**Amendment 2026-10-15 (post-execution accuracy corrections — findings drain):** all tasks
were complete and every task audit had returned PASS when a findings drain surfaced four
places where this plan's text no longer matched the shipped, audited behavior. The shipped
behavior is correct in all four; the plan was corrected to match it, and no decision row's
recorded answer was altered. F1 — §3.3's whole-body `CapToBudget` bullet and acceptance
criterion 2 now describe the shipped response-body accounting: the provenance line is emitted
whole, the assembled body is returned uncapped like its siblings `Explore` and `ForSymbol`
(only `AnalysisCandidates` re-clips), and the budget is honored through the engine's clamped
`charBudget`, Core's 1500-character per-document floor, and the 6000-character per-section
cap; the deviation from D11's design wording is recorded as a post-execution note under
`## Decisions` (D11's row itself is untouched). F2 — §3.4's miss bullet now quotes the
shipped `StandardMiss` wording and records that the Config-Reg / `ontology.technologies`
pointer lives in the runbook's troubleshooting row, not in the tool response. F5 — T5's cells
now record the executed test location
(`tests/KyberWeave.Tests/SquadToolchainRosterGuardTests.cs`, class
`SquadToolchainRosterGuardTests`, test
`ShippedToolchainRosterGrantsEveryToolTheKyberWeaveMcpServerExposes`) and the corrected
runner filter `FullyQualifiedName~SquadToolchainRosterGuardTests` — executed fact, not a
deviation needing approval. F7 — §1's stale "five tools" present-tense phrasing now
distinguishes the five tools that predate this change from the six that exist now; every
dated ledger/investigation mention (the D15 row, §2, the T6 instructions) is left verbatim
as an accurate record of what was true when written. This correction pass had no
process-execution tool in the harness either, so neither `docs validate` nor `docs drift`
was run in-session; the conductor's worker re-runs both, as every prior edit pass also
required.

**Development mode:** `test-first`. Every implementation task defines failing automated tests
before modifying production code. The one documentation task carries its own read-only
verification in the Test contract.

---

## 1. Problem and goal

An agent about to write C# or tests in this repository needs `docs/standards/csharp/README.md`
or `docs/standards/test/README.md`. Today it resolves that path from the Config Reg block of
the root `AGENTS.md` (`<csharp-coding-standard>`, `<test-coding-standard>`) and reads the whole
document. Two things are missing:

1. **A corpus-graph path to the standard.** Five MCP tools existed before this change — six
   answer questions about the corpus now that `agent_coding_standards` has shipped — but none
   of those five was keyed by technology: `docs_explore("csharp coding standard")` works only
   as a text query, and its ranking treats a coding standard like any other document. There
   was no deterministic keying on the identity the ontology already declares (`doc-type:
   coding-standard`, `technology: csharp`) — the gap this plan closed.
2. **A rule-id index.** The csharp standard states the repository's rule-id policy ("Rule ids
   are permanent … suppressions, SARIF baselines and code-scanning alerts key on them"). An
   agent keying a suppression or triaging a SARIF alert needs the `KW-*` ids a standard states,
   surfaced beside the guidance — not another grep.

**Goal:** a new read-only MCP tool **`agent_coding_standards`** on the existing
`kyber-weave-mcp` server that takes a technology key (slug or Config Reg property name),
returns the standard's relevant sections plus a rule-id index of the `KW-*` ids it states, and
is registered in the Kyber-Squad packaging so deployed squads see it.

**Non-goal:** no ontology widening, no new scoring, no CodeGraph dependency. The tool queries
the in-memory `DocumentIndex` over the corpus, exactly like the five tools that predate it.

## 2. Investigation findings

Sources (authored 2026-10-14 against `2794cc8`; re-verified 2026-10-15 against merged HEAD
`50e5ae4`): `src/KyberWeave.Mcp/DocsTools.cs`,
`src/KyberWeave.Mcp/DocsServerInstructions.cs`, `src/KyberWeave.Mcp/Program.cs`,
`src/KyberWeave.Core/Docs/Search/DocumentIndex.cs`, `DocumentIndexHost.cs`,
`src/KyberWeave.Core/Docs/Model/DocumentModel.cs`,
`src/KyberWeave.Core/Configuration/ConfigRegConfig.cs`, `DocsLayout.cs`,
`src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs`,
`products/kyber-squad/toolchain.yml`, `products/kyber-squad/bundles/full.yml`,
`products/kyber-squad/mcp.json`, `tests/KyberWeave.Tests/McpPackagingTests.cs`,
`McpDocsProvenanceTests.cs`, `SquadSourceTests.cs`,
`tests/KyberWeave.Tests/DocumentIndexTests.cs` (the T1-authored `CodingStandardsLookupTests`
block),
`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs`,
`src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`,
`docs/docgraph/mcp-runbook.md`, `docs/docgraph/retrieval.md`,
`docs/standards/csharp/README.md`, `docs/standards/test/README.md`, `.git/logs/HEAD`.
**Discovery fallback disclosure:** the 2026-10-14 authoring pass had no MCP tools, and the
2026-10-15 re-assessment used direct text search of the merged tree (self-gathered) rather
than `codegraph_explore` / `docs_explore`; every merged-tree claim below is backed by a read
of the named file. One of those reads was mistaken: the re-assessment read
`src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` while the T2 worker was writing it, and
reported T2's just-written engine as merged state. Git plumbing settled the fact after the
fact — the engine was authored in-session by T2, and the contrary claim is withdrawn
(D14's corrected row).

- **Tool surface.** `DocsTools` is the single `[McpServerToolType]`; tools register via
  `[McpServerTool]` attributes and `WithToolsFromAssembly()` in `src/KyberWeave.Mcp/Program.cs`.
  A new public method on `DocsTools` is therefore automatically exposed — no registration code.
- **Engine query authored in-session by T2, not delivered by the merge (D14, corrected
  2026-10-15).** The §3.2 engine API does not exist at merged HEAD `50e5ae4`: neither the
  HEAD blob nor the index blob of `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs`
  contains `CodingStandards`, `CodingStandardTechnologies`, `WholeExcerpt`, or
  `NormalizeTechnologyKey`, and no commit in `2794cc8..50e5ae4` touched the file. The
  members exist in the working tree (+173 lines, unstaged, purely additive) because T2
  implemented them this session per §3.2:
  `DocumentIndex.CodingStandards(string technology, string? topic = null, int charBudget =
  DefaultCharBudget)` at `DocumentIndex.cs:287`, `CodingStandardTechnologies()` at `:337`,
  `NormalizeTechnologyKey` at `:357`, and the private `WholeExcerpt` at `:675` — the
  no-topic document-order role this plan's design called `ExcerptInDocumentOrder`; T2
  adopted the name `WholeExcerpt` and nothing is renamed. The implementation matches §3.2
  on every point: suffix and angle-bracket normalization (`NormalizeTechnologyKey`),
  technology match gated on `DocType == CodingStandard` with `OrdinalIgnoreCase`,
  ordinal-by-path ordering, `Score = 1.0`, empty code joins, the 1500-character
  `MinPerDocumentBudget` floor, the 1000–120000 clamp, the rarity-weighted topic path
  through the existing `Excerpt`, and the must-include first section with named omissions.
  One divergence from §3.2's prose: an empty/whitespace key is not a caller error at the
  engine — it returns an empty result indistinguishable from a miss, and the tool layer
  must pre-check whitespace input itself to answer usage prose (§3.4, T4). The plan's
  original RED premise for T1 — a compile error against a missing API — was genuine, and
  T1's RED was recorded as exactly that, before T2 ran.
- **Contract constraints** (`src/KyberWeave.Mcp/AGENTS.md`, `mcp-runbook.md`): every response
  leads with the provenance line via `CorpusProvenance` and `Lead` (header emitted whole,
  budget taken from the body); cap everything (`SectionCharCap = 6000` per section; analysis
  surfaces cap at 12000); say when the answer is weak (miss reports considered count); name
  what was omitted; `[Description]` is routing metadata written to a tested scoring contract;
  stdout is the JSON-RPC transport. Changing a tool contract requires updating
  `docs/docgraph/mcp-runbook.md` in the same change; scoring changes require
  `docs/docgraph/retrieval.md`.
- **Server instructions are a same-change surface (D15, re-assessed 2026-10-15,
  independently verified the same day with no corrections).** Commits
  339/342 added `src/KyberWeave.Mcp/DocsServerInstructions.cs`: session-level instructions
  (`DocsServerInstructions.Text`) that enumerate all five tools and are wired through
  `AddMcpServer(DocsServerInstructions.Configure)` in `Program.cs`.
  `McpPackagingTests.ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch` pins every
  tool's presence in the text, and `src/KyberWeave.Mcp/AGENTS.md` requires the description
  and instruction surfaces stay consistent. The sixth tool therefore extends both — T3 adds
  the pinning assert, T4 edits `Text`. The description-scoring contract itself is unchanged
  by 339/342: `Trigger clause` 35 / `Specific opening` 15 / `Trigger keywords` 15, negative
  boundary 20 on `docs_explore` and 0 on every other tool
  (`tests/KyberWeave.Tests/McpPackagingTests.cs:83-85`, `:152-156`), and the
  `CorpusProvenance`/`Lead`/`SectionCharCap` mechanics §3.3 reuses are intact in the merged
  `DocsTools.cs`. Two wording tests joined since authoring —
  `DocsExploreDescriptionUsesRequesterVocabularyAndPrecedesFileSearch` and
  `McpToolDescriptionsUseGenericRepositoryDocumentationWording` — pin conventions for
  `docs_explore`'s description; the runbook's "How the descriptions are written" section
  (trigger clause the longest part, requester's vocabulary, no authority over other tools)
  governs the new description without changing §3.1's design.
- **Model already carries the key.** `DocumentModel` exposes resolved `DocType`
  (`DocType.CodingStandard`) and `Status`, `Frontmatter.Technology` (raw string), and `Sections`
  (`##` splits with `Heading`, `Body`, `LineNumber`). No ontology widening is needed for a
  standards-keyed query.
- **Config Reg linkage.** `ConfigRegConfig.Resolve` publishes one property per declared
  technology: name `technology + "-coding-standard"` (`CodingStandardSuffix`), path
  `DocsLayout.TechnologyStandard(docsRoot, technology)` = `<docs-root>/standards/<technology>/README.md`.
  So `csharp` and `csharp-coding-standard` name the same standard, and the mapping from
  property name to slug is a suffix strip — no table lookup required.
- **Corpus is the source of truth.** The standards carry `technology: csharp|test` in
  frontmatter, validated by `KW-DOC-SPEC-007` (a `coding-standard` must sit in the folder named
  by its `technology`). The tool can therefore resolve from the corpus alone; the MCP
  composition root need not inject `OntologyConfig` into `DocsTools` (it currently does not —
  `OntologyConfig` is a local in `Program.cs`, and widening the DI surface would be a larger
  change than the tool needs).
- **Excerpt machinery is reusable.** `DocumentIndex.Excerpt(doc, queryVector, budget)` ranks
  sections by rarity-weighted similarity, always includes the top-scoring section, emits
  chosen sections in document order, and returns `OmittedHeadings` plus a `BudgetExhausted`
  flag — the exact behavior `docs/docgraph/retrieval.md` documents under "Budget, not
  truncation". A no-topic variant (document order, same must-include and named-omissions
  contract) is a small addition that introduces no new weights or thresholds.
- **Rule-id facts.** No `KW-*` id appears in either standard today (the csharp standard states
  the *policy*, not the ids). So the rule-id index must be honest when empty. Ids that do
  appear in governed prose follow `KW-<AREA>-<NUMBER>` (e.g. `KW-DOC-SPEC-007`,
  `KW-MCP-ROOT-002`).
- **Squad packaging (re-assessed 2026-10-15).** `products/kyber-squad/mcp.json` still
  declares the *server* (`kyber-weave-mcp --repo-root .`), not tools, and the per-tool roster
  is still `products/kyber-squad/toolchain.yml` `required-mcp-tools.kyber-weave` — the
  restructure that added `products/kyber-squad/bundles/` did not move it (`bundles/full.yml`
  is a `kyber-squad.bundle/v1` agents+skills manifest with no MCP roster). `ZCodeRenderer`
  (`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs:174`, `:953-980`) still grants by
  fully qualified name from `source.Toolchain.RequiredMcpTools`, and `squad doctor`
  (`SquadDoctorCommand.ReportZCodeMcpConfiguration`) still treats each roster server as an
  install requirement. Commit `f14bfb1` removed project-level squad deployment from this
  repository (`.github/agents/**` is gone), but the product source remains at
  `products/kyber-squad/`, is still loaded by tests (`SquadSourceTests` uses
  `Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad")`) and still validated
  by release packaging (`docs/distribution.md` "Toolchain Qualification"), so DEC-4's premise
  — register the tool so deployed squads see it — stands and T5 stays in this change. Squad
  agent profiles still grant `kyber-weave/*`. **Roster gap (D16, verified 2026-10-15,
  answered at the same-day gate):** the roster lists four
  kyber-weave tools and is missing `docs_status` — its header says it was last verified
  2026-09-21, before `docs_status` shipped (2026-09-29). The approved T5 parity guard
  ("every `DocsTools` `[McpServerTool]` name appears in the roster") therefore fails on that
  pre-existing gap as well as on `agent_coding_standards`; the user's answer — add both
  names, no carve-out — is recorded in D16's row.
- **Tests that pin the tool contract and must change in the same change:**
  - `McpPackagingTests`: theory rows per tool — routing-metadata score (Trigger clause 35,
    Specific opening 15, Trigger keywords 15), `ReadOnly`/`OpenWorld` annotations, and
    "only the broadest tool carries a negative boundary" (the new tool must score 0 there).
  - `McpDocsProvenanceTests`: `AllResponses` / miss-and-empty coverage — every tool response,
    including misses, must lead with the provenance line. Verified 2026-10-15: `AllResponses`
    is a private helper (`McpDocsProvenanceTests.cs:171`) whose existing entries call the
    tool methods directly, and `MissAndEmptyResponsesStillLeadWithProvenance` sits at `:56`
    — so T3's sixth-tool entries must use the file's reflection+Invoke form (the one it
    already uses for `docs_status`), never a direct call.
  - `McpPackagingTests.PackagedMcpConfigurationBindsTheServerToTheCurrentRepository` pins
    `mcp.json` contents (unaffected — no change to that file).
- **Staleness** is solved by `DocumentIndexHost.Current()` (two-clock rebuild of corpus and
  joins). Tools add no caching. The new tool follows the same pattern.

## 3. Design — the tool contract

### 3.1 Signature

```csharp
[McpServerTool(Name = "agent_coding_standards", ReadOnly = true, OpenWorld = false)]
public string AgentCodingStandards(
    string technology,
    string? topic = null,
    int charBudget = DocumentIndex.DefaultCharBudget)
```

| Parameter | Contract |
|---|---|
| `technology` (required) | A technology slug (`csharp`, `test`) or the Config Reg property name that publishes it (`csharp-coding-standard`, `<csharp-coding-standard>`), case-insensitive. |
| `topic` (optional) | What the caller is about to do. Omit to get the whole standard within budget. Provide to get the sections most relevant to that work, chosen by the same rarity-weighted section ranking `docs_explore` uses. |
| `charBudget` (optional) | Total characters for the response body (clamped 1000–120000, default 12000). The provenance line is emitted whole; the budget is taken from the body. Sections that did not fit are named by heading. |

The `[Description]` is routing metadata per the runbook's description rules: capability clause
("Returns the coding standard for one declared technology — the relevant sections plus a
rule-id index of the KW-* ids it states"), trigger conditions (before writing or reviewing
code in a technology, when keying a suppression or triaging a SARIF/code-scanning alert on a
rule id, when a portable agent needs the local standard rather than its shipped default), and
response semantics (an empty result means the corpus declares no standard for that
technology). It states its territory positively and carries no exclusion (only `docs_explore`
owns one). Re-verified 2026-10-15 against the runbook's "How the descriptions are written"
section (added by commit 339): the trigger clause is the longest part and the wording follows
the requester's vocabulary — no change to this design, which already followed both. No
`stdout` writes; all output is the return string.

### 3.2 Key resolution (engine)

**Implementation status (D14, corrected 2026-10-15):** this API is authored in-session by
T2, not delivered by the merge — see the §2 bullet "Engine query authored in-session by
T2". T2 implemented it per this design and the task is complete; the plan's name
`ExcerptInDocumentOrder` below is the design's name for what T2 shipped as the private
`WholeExcerpt` (`DocumentIndex.cs:675`); T2 adopted that name rather than renaming
anything.

New public method on `DocumentIndex`:

```csharp
public IReadOnlyList<DocumentHit> CodingStandards(string technology, string? topic = null, int charBudget = DefaultCharBudget)
public IReadOnlyList<string> CodingStandardTechnologies()
```

- Normalize the key: trim; strip one surrounding pair of `<`/`>` if present; strip the literal
  suffix `-coding-standard` (`ConfigRegConfig.CodingStandardSuffix`) case-insensitively; trim.
  At the engine an empty or whitespace key returns an empty result (not distinguishable from
  a miss there); the *tool* treats it as a caller error and answers usage prose by
  pre-checking whitespace input itself before calling the engine (§3.4, T4, D14).
- Match documents with `DocType == CodingStandard` whose trimmed `Frontmatter.Technology` equals
  the normalized key, `OrdinalIgnoreCase`; order by `RelativePath` (ordinal). Matching is
  lenient on input case as a convenience; the corpus value is echoed verbatim in the response's
  `technology:` line, and the validator keeps the corpus itself exact.
- `CodingStandardTechnologies()` returns the distinct declared `technology` values across
  coding-standard documents, ordinal-sorted — the hint a miss needs.
- Excerpts: with `topic`, reuse the existing private `Excerpt` with a rarity-weighted fused
  topic vector (`_corpus.WeightByRarity(TextVectorizer.VectorizeFused(topic))`) — identical
  selection to `docs_explore`, so no new scoring exists. Without `topic`, a new private
  `ExcerptInDocumentOrder(doc, budget)` emits sections in document order under budget,
  preserving the same contract: the first section is always included even if it alone exceeds
  the budget (truncated at the transport edge), and anything dropped is named.
- `charBudget` is clamped like `Explore` (1000–120000). Multiple matching standards (the
  one-standard-per-technology shape violated) split the budget with the same 1500-character
  per-document floor. Hits carry `Score = 1.0` (deterministic keying, not ranking), the
  excerpt, and `DocumentExcerpt.Empty` joins — coding standards carry no `code-refs` by
  ontology design, so no CodeGraph dependency is introduced.

### 3.3 Response shape (hit)

The provenance line (whole), then the body, in this order — rule ids precede sections so body
budget truncation can never drop the index:

```
provenance: root=/… rev=… dirty=… documents=…
1 standard for technology 'csharp' (status CURRENT).
### C# coding standard
path: docs/standards/csharp/README.md
id: standards/csharp
doc-type: CODING-STANDARD  status: CURRENT  component: (none)
technology: csharp
rule ids: KW-DOC-SPEC-007 · …
## <heading>
<section body, per-section truncation at 6000 characters>
…
[complete document]                                   ← or the omitted-headings line
```

- Identity block reuses `AppendIdentity` (title, path, id, doc-type, status, component) plus a
  `technology:` line.
- **Rule-id index (DEC-3):** scan the standard's `Body` with the pattern
  `\bKW-[A-Z0-9]+(?:-[A-Z0-9]+)*\b`; dedupe preserving first occurrence (document order); hard
  cap 50 with an explicit `… N more` line. When the standard states none, emit
  `rule ids: (this standard states no rule ids)` — an honest empty, never a fabricated index.
- **Status honesty:** when the standard's status is not `CURRENT`, add a warning line naming
  the status (DRAFT / NEEDS-REVIEW / SUPERSEDED) so the caller knows the guidance is
  provisional. The standard is still returned — a draft standard is better than a false miss.
- Omitted headings reuse the `AppendExcerpt` semantics verbatim: `[complete document]`,
  `[omitted for space, ask again with a larger charBudget: …]` versus
  `[also in this document, not matching this topic: …]`.
- A standard with zero `##` sections yields an explicit note naming its path (rare; the
  validator does not require sections) rather than an apparently empty document.
- The assembled body is **not** re-clipped as a whole (post-execution correction,
  2026-10-15 findings drain — the original wording here specified a whole-body `CapToBudget`
  clip to `clamped(charBudget) − provenance.length − 1`, the accounting
  `AnalysisCandidates` uses). The hit path returns `Lead(provenance, sb.ToString())`
  uncapped, exactly like the sibling retrieval tools `Explore` and `ForSymbol`; the budget is
  honored where the content is produced — the tool clamps `charBudget` (1000–120000) before
  calling the engine, Core's excerpt selection honors the budget with a 1500-character
  per-document floor, and each section body is capped at `SectionCharCap` (6000) — so any
  overshoot past the requested budget is bounded, and a whole-body clip would have truncated
  the tail — the `[omitted for space …]` marker — at exactly the small budgets where the
  caller needs it. The header is emitted whole via `Lead`. The deviation from the original
  design wording is recorded as a post-execution note under `## Decisions`.

### 3.4 Miss and error behavior

- Unknown or undeclared technology: a real miss, reported as such — "No coding standard for
  'x' is declared here. This repository declares a standard for: csharp, test. N documents
  were considered, so this is a real miss rather than a lookup that failed: that technology
  is not standardised in this repository, and the answer for it is not written down here."
  — the shipped `StandardMiss` wording (`src/KyberWeave.Mcp/DocsTools.cs`); when the corpus
  declares no standard at all the offer line reads "This repository declares no coding
  standard for any technology." The response itself carries no pointer to the Config Reg block
  or `ontology.technologies` — that pointer lives in the runbook's troubleshooting row for
  this tool (`docs/docgraph/mcp-runbook.md`, "`agent_coding_standards` reports no standard
  for a technology"). Still provenance-led. (Post-execution correction 2026-10-15, findings
  drain — the original wording here had the pointer inside the tool response.)
- Empty/whitespace `technology`: usage prose naming both accepted forms
  (`'csharp'`, `'<csharp-coding-standard>'`). Still provenance-led.

### 3.5 Staleness and joins

`_host.Current()` per call — the two-clock rebuild keeps the answer fresh after documentation
edits; no tool-layer caching is added. No CodeGraph index is consulted; a missing `.codegraph/`
cannot degrade this tool's answers.

### 3.6 Same-change documentation

- `docs/docgraph/mcp-runbook.md` **must change**: "All five are declared `ReadOnly`" becomes
  six; a new `### agent_coding_standards(technology, topic?, charBudget = 12000)` section
  documents the parameters, the slug/property-name resolution, the rule-id index and its
  honest-empty behavior, and the empty-result meaning; a troubleshooting row is added
  ("`agent_coding_standards` reports no standard for a technology" — wrong spelling, not a
  declared technology, or no standard scaffolded; check the Config Reg block and
  `ontology.technologies`).
- `docs/docgraph/retrieval.md` **does not change**: no weight, threshold, floor, or severity
  changes; section selection reuses the documented "Budget, not truncation" behavior. The
  runbook documents the tool-specific contract (see D8).
- `src/KyberWeave.Mcp/AGENTS.md` **does not change**: it states tool-contract rules generally
  ("adding a tool means adding an `[McpServerTool]` method…") and does not enumerate tools;
  the runbook is the enumerated surface.
- **Server instructions (D15):** `src/KyberWeave.Mcp/DocsServerInstructions.cs` **changes in
  T4** — `Text`'s "Also available" roster gains the sixth tool — and its pinning test
  (`McpPackagingTests.ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch`) gains the
  tool in T3. `Program.cs` is untouched: `AddMcpServer(DocsServerInstructions.Configure)` is
  already wired. Verified 2026-10-15 with no corrections; the amended T3/T4 cells were
  approved by the user at the 2026-10-15 conductor gate.
- **Kyber-Squad packaging (DEC-4, D16):** add both `agent_coding_standards` and
  `docs_status` to `products/kyber-squad/toolchain.yml` under `required-mcp-tools.kyber-weave`
  (the roster `ZCodeRenderer` grants from and `squad doctor` checks against) — D16's answered
  outcome, 2026-10-15 gate, no carve-out. `products/kyber-squad/mcp.json`
  needs no edit — it declares the server, not tools (verified in investigation; pinned by
  `McpPackagingTests.PackagedMcpConfigurationBindsTheServerToTheCurrentRepository`). A new
  parity guard test (T5) makes the roster drift-proof against `DocsTools`.

## 4. Decisions

Permanent decision ledger, preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| DEC-1 | What is the query key for the standards lookup? | **Technology slug / Config Reg property name.** Technology keying only — no agent-name or squad-name lookup. | A technology already names its standard (`ConfigRegConfig.Resolve` publishes `<technology>-coding-standard`; `KW-DOC-SPEC-007` pins folder = technology). Agent/squad keying would need a second mapping this corpus has no ontology for. | User decision, recorded verbatim from conductor intake 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| DEC-2 | What surface, and what name? | A **new dedicated MCP tool** named exactly **`agent_coding_standards`**. | Departing from the `docs_` prefix is a deliberate user choice; consequences handled: the runbook roster lists it beside the `docs_*` tools, the description states its territory positively, and `toolchain.yml` matches by exact name so no prefix assumption exists anywhere. | User decision, recorded verbatim from conductor intake 2026-10-14 (name is user-supplied; use exactly this name); approved by the user at the approve-and-execute gate 2026-10-14. |
| DEC-3 | What does it return? | **The relevant sections of the standard plus a rule-id index** — the `KW-*` ids the standard states, so the agent can key suppressions/alerts on them. | Neither current standard states concrete `KW-*` ids, so the index must report its emptiness honestly rather than fabricate (`(this standard states no rule ids)`); it becomes populated the moment a standard cites ids, with no schema change. Extraction is text-based, not a new frontmatter key — no ontology widening. | User decision, recorded verbatim from conductor intake 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| DEC-4 | Deployment scope? | Ship in this repository's MCP server **and** register the tool in the squad packaging at `products/kyber-squad/mcp.json` so deployed squads see it. | Verified in investigation: `mcp.json` declares the server, not tools — the operative per-tool registration is `products/kyber-squad/toolchain.yml` `required-mcp-tools.kyber-weave` (the roster `squad doctor` checks and `ZCodeRenderer` grants from). The task adds the roster entry, keeps `mcp.json` unchanged, and adds a parity guard so the roster cannot drift from `DocsTools` again. Agent capability profiles already grant `kyber-weave/*`. | User decision, recorded verbatim from conductor intake 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| DEC-5 | Development mode? | `development-mode: test-first` — every implementation task gets a Test contract naming test surface, runner, and observable behavior with RED/GREEN evidence rules. | Default mode; the user did not opt out at intake. | User decision (default accepted), conductor intake 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D6 | Exact query inputs? | `technology` (required string), `topic` (optional string), `charBudget` (optional int, default `DocumentIndex.DefaultCharBudget` = 12000, clamped 1000–120000). | `topic` is a relevance filter on the caller's work, not an agent-name lookup — DEC-1 forbids the latter. Budget bounds and default mirror `docs_explore` so one mental model serves both tools. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D7 | How does a Config Reg property name resolve to a technology? | Trim, strip one surrounding `<>` pair, strip the case-insensitive literal suffix `-coding-standard` (`ConfigRegConfig.CodingStandardSuffix`), trim; match `DocType == CodingStandard` documents by `Frontmatter.Technology` `OrdinalIgnoreCase`. The corpus is the source of truth; `OntologyConfig` is not injected into `DocsTools`. | The suffix is the exact constant `docs init` uses to publish the property, so slug ↔ property-name is a pure function. Corpus-side resolution avoids widening the MCP DI surface (`OntologyConfig` is a composition-root local today) and keeps staleness solved by `DocumentIndexHost` alone. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D8 | How are "relevant sections" chosen without contradicting `retrieval.md`? | With `topic`: reuse the existing `Excerpt` section ranking (rarity-weighted, damped, document-order emission, named omissions) — the behavior `retrieval.md` already documents. Without `topic`: document-order whole-standard under budget, same must-include-top-section and named-omissions contract. No new weights, thresholds, or floors; `retrieval.md` unchanged; the tool-specific contract is documented in `mcp-runbook.md`. | A standard is one file, so keying — not ranking — finds it; a fresh scoring scheme would be undocumented contract by construction. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D9 | Rule-id index mechanics? | Regex `\bKW-[A-Z0-9]+(?:-[A-Z0-9]+)*\b` over the standard's `Body`; dedupe preserving document order; hard cap 50 with an explicit `… N more` line; emitted **before** the sections so budget truncation cannot drop it; honest `(this standard states no rule ids)` when empty. | `Body` excludes frontmatter, which never states ids. The cap bounds a pathological standard; placement protects the one part an agent keys suppressions on. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D10 | Missing-technology and non-current-status behavior? | Unknown technology: an explicit miss listing the technologies the corpus *does* declare and how to add one. Empty input: usage prose. A standard with non-`CURRENT` status is still returned, with a warning line naming the status. | House style: a miss says it is a real miss and how many documents were considered, so the caller knows it may fall back; a draft standard is better than a false miss, and hiding status would be dishonest. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D11 | Caps, provenance, staleness? | Reuse `SectionCharCap` (6000) per section and the 1000–120000 budget clamp; provenance line emitted whole via `Lead`, budget from the body (`CapToBudget`); staleness via `_host.Current()` per call, no tool-layer caching; no CodeGraph reads. | Matches the existing five tools' contract exactly (`Mcp/AGENTS.md`, runbook); `McpDocsProvenanceTests` and `McpPackagingTests` then extend mechanically. | ANSWERED — architect-settled under the conductor's planning mandate 2026-10-14; approved by the user at the approve-and-execute gate 2026-10-14. |
| D12 | Shared-assembly scheduling: T1 and T3 both add tests to the same test assembly — keep the approved T1 ∥ T3 concurrency or serialize? | **Serialize T1 → T2 → T3 → T4, and author T3's tool-response tests with the reflection+Invoke idiom** (`FindMcpTool` + `MethodInfo.Invoke`, the pattern `McpDocsProvenanceTests` uses for `docs_status`) **so the test assembly compiles at T3's RED point.** | T1's direct-call engine tests break compilation until T2 lands; under T1 ∥ T3 concurrency both tasks' RED evidence collapses onto that one compile error, T2's GREEN ("T1 GREEN") is unobservable, and any T3 test calling the absent tool directly keeps the assembly broken until T4. The repo's own tests already compile against a not-yet-existing tool: `McpPackagingTests.McpToolMethod` (`tests/KyberWeave.Tests/McpPackagingTests.cs:126-133`) reflects over `[McpServerTool]` metadata, and `McpDocsProvenanceTests` (`tests/KyberWeave.Tests/McpDocsProvenanceTests.cs:138-142`) invokes `docs_status` via `FindMcpTool` + `Invoke`. Rejected: (a) keeping T1 ∥ T3 with deferred, indistinguishable RED evidence — evidence must be attributable at the task boundary; (b) reflection-only T3 with behavior calls deferred — splits the approved T3 contract. The amendment edits the T3 RED-evidence cell and the T2 GREEN-acceptance cell, reopening those two cells for user re-approval; every other approval stands. | ANSWERED — architect-settled under the conductor's planning mandate (scheduling-hazard adjudication) 2026-10-14; the amended T3-RED and T2-GREEN contract cells reopened at the 2026-10-14 conductor gate and were approved by the user — verbatim answer: "Approve amended contracts and resume" — so the amendment and both amended cells stand approved. |
| D13 | Concurrency cap on the task graph? | **No concurrency cap anywhere.** Dispatch is governed by readiness: every task whose declared dependencies are satisfied is dispatched immediately, with no limit on parallelism; only the dependency graph (including D12's T1 → T2 → T3 → T4 chain) and each task's file/symbol scope constrain order. The former `MAX_CONCURRENCY: 2` declaration is removed from §6. | The conductor's execution contract already states dispatch happens with no concurrency cap, so the plan's cap declaration was a contradiction of the execution contract, not a safety property. T1–T4's serial order is the D12 dependency chain — a dependency fact, not a cap; T5 ∥ T6 is an ordering observation (both ready at T4, no mutual dependency), not a cap. | ANSWERED — user instruction at the 2026-10-14 conductor gate, verbatim: "no max concurrency every!!! remove max concurrency from everything"; applied to §6 of this plan in the same amendment pass as D12's approval. |
| D14 | Post-merge re-assessment (fast-forward `2794cc8..50e5ae4`, 2026-10-15): did merged `origin/main` deliver the §3.2 engine query — `DocumentIndex.CodingStandards` and `CodingStandardTechnologies` at HEAD — voiding T1's RED premise? | **No — the claim is withdrawn; T1/T2 stand as originally approved and are complete.** Git-plumbing verification established that the engine query was authored in-session by T2, not delivered by the merge: the HEAD and index blobs of `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` contain none of `CodingStandards`, `CodingStandardTechnologies`, `WholeExcerpt`, `NormalizeTechnologyKey`; no commit in `2794cc8..50e5ae4` touched the file; and the members exist only as unstaged working-tree additions (+173 lines, purely additive; HEAD blob 613 lines, working tree 786). The re-assessment had read the file while the T2 worker was writing it and mistaken T2's just-written code for merged state — a race artifact. T1's RED was genuine: 18 `CS1061` compile errors against the then-absent members, recorded before T2 ran. T2 was genuine implementation, now green (32/32 in `CodingStandardsLookupTests`, 48/48 across `DocumentIndexTests.cs`'s four classes, 2830/2830 project-wide, format and analyzer gates clean). The reopen it caused is void and the T1/T2 contract cells are restored to their D12-amended content. Two facts the mis-read surfaced are retained because they describe T2's real code: T2 realized the design's `ExcerptInDocumentOrder` as the private `WholeExcerpt` (`DocumentIndex.cs:675`), and the engine folds an empty/whitespace key into an empty result indistinguishable from a miss, so the tool pre-checks whitespace input itself and answers usage prose (§3.4, T4). | **Provenance:** verified over the commit range `2794cc8..50e5ae4` (HEAD = `50e5ae4`) by git plumbing — `git show HEAD:<file>` and `git show :<file>` match none of the four members, `git log 2794cc8..50e5ae4 -- <file>` is empty (no commit in the range touched the file), and `git diff --stat HEAD -- <file>` reports 1 file changed, 173 insertions (working tree only, unstaged). **The D14 reopen was withdrawn after this git-plumbing verification**; the earlier contrary reading had observed the working tree mid-write, not the merged state. D12's serial chain T1 → T2 → T3 → T4 stands with its original T1-compile-break rationale restored; T3's reflection+Invoke idiom remains required for the still-absent tool method. | WITHDRAWN 2026-10-15 — the reopen (Draft status, T1/T2 re-scope to verify/reconcile) is reverted and the plan returns to Ready exactly as it stood after the D12/D13 amendments and their user approval; every prior approval (DEC-1 to DEC-5, D6–D13, both 2026-10-14 gates) stands. |
| D15 | The same merge added a server-instructions surface (commits 339/342): `src/KyberWeave.Mcp/DocsServerInstructions.cs` enumerates all five tools and `McpPackagingTests.ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch` pins each tool's presence — does the sixth tool extend that surface in this change? | **Yes, as a mandatory same-change edit.** T3 adds `agent_coding_standards` to the pinning test's enumeration; T4 adds the tool to `DocsServerInstructions.Text`'s "Also available" roster. `Program.cs` is untouched — `AddMcpServer(DocsServerInstructions.Configure)` is already wired and pinned by `McpPackagingTests.McpServerHostRegistersTheDocumentationInstructions`. | `src/KyberWeave.Mcp/AGENTS.md` requires the description surface and `DocsServerInstructions` stay consistent; leaving the sixth tool out of the instructions while adding it to `DocsTools` would fail the existing pinning test, so this is not a choice but a consequence of an existing repo contract. The 339/342 changes did **not** alter the description-scoring contract this plan reuses: `Trigger clause` 35 / `Specific opening` 15 / `Trigger keywords` 15, negative boundary 20 on `docs_explore` and 0 on every other tool (`tests/KyberWeave.Tests/McpPackagingTests.cs:83-85`, `:152-156` — refs re-verified 2026-10-15), and `CorpusProvenance`/`Lead`/`SectionCharCap` are intact in merged `DocsTools.cs`. §3.1's description design stands, now also honoring the runbook's "How the descriptions are written" conventions (trigger clause the longest part, requester's vocabulary). | ANSWERED — architect-settled under the conductor's planning mandate, 2026-10-15 (follows from an existing repo contract, not a new choice). **Independently verified 2026-10-15, confirmed with no corrections:** `src/KyberWeave.Mcp/DocsServerInstructions.cs` exists as `public static class DocsServerInstructions` with `public const string Text` enumerating the five tools in the order `docs_explore`, `docs_for_symbol`, `docs_glossary`, `docs_analysis_candidates`, `docs_status`, and `public static void Configure(McpServerOptions options)` setting `options.ServerInstructions = Text`; wired at `src/KyberWeave.Mcp/Program.cs:80` via `AddMcpServer(DocsServerInstructions.Configure).WithStdioServerTransport().WithToolsFromAssembly()`; pinned by `ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch` (`tests/KyberWeave.Tests/McpPackagingTests.cs:115` — reads `Text` at :117, asserts the tool names at :119-124, round-trips `Configure` at :126-128) and `McpServerHostRegistersTheDocumentationInstructions` (`:132`); the scorer still pins Trigger clause 35 / Specific opening 15 / Trigger keywords 15 (`:83-85`) and the negative boundary 20 on `docs_explore` / 0 on the other four (`:152-156`); `src/KyberWeave.Mcp/AGENTS.md` requires the two surfaces stay consistent verbatim. Amends the T3 observable-behavior cell and the T4 row: both amended cells were approved by the user at the 2026-10-15 conductor gate — verbatim: "Approve: extend the server instructions and add the pinning assert" — so the amendment and both amended cells stand approved and T3/T4 may execute. |
| D16 | The squad roster `products/kyber-squad/toolchain.yml` `required-mcp-tools.kyber-weave` lists four tools and is missing `docs_status` (roster header last verified 2026-09-21; `docs_status` shipped 2026-09-29). The approved T5 parity guard — every `DocsTools` `[McpServerTool]` name appears in the roster — will fail on that pre-existing gap too. Add `docs_status` to the roster, or carve it out of the guard? | **Add both `docs_status` and `agent_coding_standards` to the roster — option (a), as recommended; the parity guard carries no carve-out.** T5 edits `products/kyber-squad/toolchain.yml` `required-mcp-tools.kyber-weave` to list both names (stable order preserved), and the guard requires every `DocsTools` `[McpServerTool]` name to appear there with no documented exception. Rationale: the roster's own header says it is the canonical list that drifts when a tool is added or renamed, root `AGENTS.md` instructs agents to call `docs_status` before trusting docs answers, and a deployed squad without it cannot run that check. Rejected option (b) — a documented `docs_status` carve-out (diagnostic tool) leaving the roster unchanged: a smaller shipped diff, but a standing exception the guard can never remove and an unfixed instance of the exact drift the guard exists to catch. | **Independently verified 2026-10-15, confirmed with no corrections:** `products/kyber-squad/toolchain.yml` exists with `required-mcp-tools` keys `codegraph`, `context7`, `kyber-weave`; the `kyber-weave` roster is exactly `docs_analysis_candidates`, `docs_explore`, `docs_for_symbol`, `docs_glossary` in that order, and `docs_status` is absent; `products/kyber-squad/bundles/full.yml` exists with `schema: kyber-squad.bundle/v1` and carries no MCP tool roster (the worker report that the roster moved there is wrong); grants flow from `ZCodeRenderer.QualifiedMcpToolNames` (`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs:956-963`) and `squad doctor` checks the roster at `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs:213-269`. `f14bfb1` removed project-level squad deployment from this repository (`.github/agents/**` is gone) but the product source remains at `products/kyber-squad/`, still loaded by `SquadSourceTests` and still validated by release packaging (`docs/distribution.md` "Toolchain Qualification"), so DEC-4's premise stands and T5 stays in this change, targeting the unchanged roster path. | ANSWERED — user decision at the 2026-10-15 conductor gate, verbatim: "Add both docs_status and agent_coding_standards to the roster (recommended)"; the prior pending-verification clause is discharged — the worker re-check confirmed the roster gap against the real `products/kyber-squad/toolchain.yml` with no corrections. The T5 RED/GREEN cells are amended to the answered outcome — both names, no carve-out — and stand approved by the same gate; T5 is unblocked once T4 lands. |

**Post-execution note (2026-10-15 findings drain) — body-budget accounting deviation from
D11's design wording.** D11's recorded answer above stands verbatim; this note records the
deviation, it does not amend D11 or its approval. The shipped hit path (T4,
`AgentCodingStandards`) deliberately does **not** re-clip the assembled body with
`CapToBudget`: it returns `Lead(provenance, sb.ToString())` uncapped, exactly like the two
sibling retrieval tools `Explore` and `ForSymbol` — only `AnalysisCandidates` re-clips its
assembled body. A whole-body cap would truncate the tail — the `[omitted for space …]`
marker — at exactly the small budgets where the caller needs it, and the overshoot past the
requested budget is bounded regardless: the tool clamps `charBudget` (1000–120000) before
calling the engine, Core's excerpt selection honors the budget with a 1500-character
per-document floor, and each section body is capped at `SectionCharCap` (6000). §3.3's
bullet and acceptance criterion 2 were corrected 2026-10-15 to describe this executed
accounting; the task audits passed against the shipped behavior, and no approval is
reopened.

## 5. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/DocumentIndexTests.cs` — new `CodingStandardsLookupTests` block authored in this task | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DocumentIndexTests"` | `CodingStandards` resolves by slug, by bare property name, and by angle-bracketed property name; case-insensitive; unknown technology returns empty; `CodingStandardTechnologies` lists declared values in ordinal order; `topic` selects matching sections and names the rest; no-topic returns document-order sections with the must-include and named-omissions contract. | A compile error naming the missing `DocumentIndex.CodingStandards` / `CodingStandardTechnologies` — **recorded 2026-10-15 as 18 `CS1061` errors** against the then-absent members (15 naming `CodingStandards`, 3 naming `CodingStandardTechnologies`), before T2 ran (genuine RED; see D14's corrected row). | Same tests pass without weakening assertions; pre-existing `DocumentIndexTests` stay green. **Complete 2026-10-15: GREEN recorded at T2's boundary.** |
| T2 | Engine implementation — `DocumentIndex.CodingStandards` / `CodingStandardTechnologies` per §3.2 in `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs`. T1 is its contract. | (T1 runner) | Per §3.2: `NormalizeTechnologyKey` (slug / bare property / angle-bracketed, case-insensitive), `DocType == CodingStandard` gating, ordinal path ordering, `Score = 1.0`, empty joins, 1500 floor, 1000–120000 clamp, topic path via `Excerpt`, no-topic path via `WholeExcerpt` (the design's `ExcerptInDocumentOrder`). No edits to `Explore`, `ForSymbol`, scoring constants, or `DocumentIndexHost`. | — | The test assembly compiles — T1's RED resolved — and T1's tests pass (the GREEN observable at T2's boundary; D12). **Complete 2026-10-15: 33/33 in `CodingStandardsLookupTests`, 49/49 across `DocumentIndexTests.cs`'s four classes, 2831/2831 project-wide, format and analyzer gates clean.** |
| T3 | `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs` (new file) + updated `McpPackagingTests.cs`, `McpDocsProvenanceTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~McpAgentCodingStandardsTests\|FullyQualifiedName~McpPackagingTests\|FullyQualifiedName~McpDocsProvenanceTests"` | Hit response leads with the provenance line; identity + `technology:` line; rule-id index extracted, deduped, honest when empty; miss lists declared technologies and considered count; empty input returns usage prose; per-section truncation at 6000 and named omissions under budget; draft standard carries a status warning; packaging theories cover `agent_coding_standards` (routing score, `ReadOnly`/closed-world, zero negative boundary); `DocsServerInstructions.Text` names `agent_coding_standards` (D15 — extend `ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch`'s enumeration; amended cell approved at the 2026-10-15 gate); provenance miss coverage includes the new tool. | Recorded after T2 GREEN and before T4 (D12). T3's tool-response tests are authored with the reflection+Invoke idiom — the `FindMcpTool` + `MethodInfo.Invoke` pattern `McpDocsProvenanceTests` already uses for `docs_status` — so the test assembly compiles against the absent method; RED is the tool-existence reflection assert failing at runtime ("DocsTools must expose an MCP tool named 'agent_coding_standards'") — never a compile error (the tool method does not exist until T4, so reflection remains the only compiling form; D12). | Same tests pass; no assertion weakened; `McpPackagingTests` scoring components unchanged. |
| T4 | Tool implementation — T3 is its contract | (T3 runner) | `DocsTools.AgentCodingStandards` per §3.1, §3.3, §3.4; `DocsServerInstructions.Text` gains the sixth tool in its "Also available" roster (D15 — amended cell approved at the 2026-10-15 gate). | — | T3 GREEN; stdout untouched (no `Console` writes in the new code path); `Program.cs` untouched. |
| T5 | Parity guard test — executed in the new file `tests/KyberWeave.Tests/SquadToolchainRosterGuardTests.cs`, class `SquadToolchainRosterGuardTests`, test `ShippedToolchainRosterGrantsEveryToolTheKyberWeaveMcpServerExposes` (post-execution correction 2026-10-15, findings drain: the plan had said `McpAgentCodingStandardsTests.cs` or `SquadSourceTests.cs`; the guard is server-versus-roster parity rather than tool behavior, so the dedicated file is the defensible placement — executed fact, not a deviation) — over the real `products/kyber-squad` source, loaded via `Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad")` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadToolchainRosterGuardTests"` | Every `[McpServerTool]` name exposed by `DocsTools` appears in the shipped `toolchain.yml` `required-mcp-tools.kyber-weave` — on the merged tree the missing names are `agent_coding_standards` **and** the pre-existing `docs_status` gap (D16 — answered 2026-10-15: both names are added to the roster, no carve-out); `mcp.json` still binds the server to the repo root and lists no tools. | RED after T4 with the roster not yet updated: the parity test fails naming every `DocsTools` tool absent from the roster — `agent_coding_standards` and `docs_status`, both of which D16's answered outcome (2026-10-15 gate) adds to the roster with no carve-out. | GREEN after the `toolchain.yml` edit implements D16's answered outcome (2026-10-15 gate: list both `agent_coding_standards` and `docs_status`; no carve-out; stable order preserved); existing squad suite (loads the real product source) stays green. **D16 answered 2026-10-15 — T5 unblocked once T4 lands.** |
| T6 | Documentation — genuinely no unit test | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` then `-- docs drift .` | `mcp-runbook.md` documents the sixth tool, its parameters, key resolution, rule-id index, and empty-result semantics; troubleshooting row added; "All five" corrected; no other governed doc changes. | Read-only verification replaces a test: both commands report zero findings with the runbook edit in place. | Both commands zero findings; `docs drift` skipped-and-reported if no usable CodeGraph index exists (per root `AGENTS.md`). |
| T7 | Whole-change verification — no new test surface | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- review gates . --out artifacts/gates.json` (or the full root-`AGENTS.md` gate list) | All declared gates pass, including the .NET suite, docs validate, and docs drift. The declared gate suite does not run `--merge-ready` (§7.2); merge-ready validation is the closeout check after §7.4's archival, not an in-flight gate. | — | Gate report green; review council run per the repo review process; findings fixed before closeout. |

## 6. Tasks (dependency order)

- **T1 — RED: engine contract tests** (`test-dev`). Author the `CodingStandardsLookupTests`
  block in `tests/KyberWeave.Tests/DocumentIndexTests.cs` (fixture corpus with two coding
  standards plus the two documents that must never answer a key, one section past 6000
  characters, one body citing `KW-DOC-SPEC-007`, one citing none; `DocFixture` +
  `FakeCodeGraphResolver`). RED is the compile error against the absent
  `DocumentIndex.CodingStandards` / `CodingStandardTechnologies`. **Complete 2026-10-15:
  RED recorded as 18 `CS1061` errors before T2 ran — 15 naming `CodingStandards`, 3 naming
  `CodingStandardTechnologies`. T1's pass-1 audit found the padded-`technology` clause
  untested and closed the gap with a mutation-proven test.**
- **T2 — GREEN: implement the engine query** (`csharp-dev`). Implement
  `CodingStandards(string, string?, int)`, `CodingStandardTechnologies()`, and the private
  `WholeExcerpt` in `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` per §3.2 (+173
  lines, purely additive). No edits to `Explore`, `ForSymbol`, scoring constants, or
  `DocumentIndexHost`. Record GREEN per T1: the assembly compiles and T1's tests pass
  before T3's tests are authored (D12's serial boundary). **Complete 2026-10-15: 33/33 in
  `CodingStandardsLookupTests`, 49/49 across `DocumentIndexTests.cs`'s four classes,
  2831/2831 project-wide, format and analyzer gates clean.**
- **T3 — RED: tool and packaging tests** (`test-dev`). Runs after T2 GREEN (D12): the test
  assembly must compile at this task's RED point. Create
  `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs` (behaviors per the T3 contract row;
  reuse the provenance-line regex from `McpDocsProvenanceTests`) and author the tool's
  response asserts with the reflection+Invoke idiom — `FindMcpTool("agent_coding_standards")`
  + `MethodInfo.Invoke`, the pattern `McpDocsProvenanceTests` already uses for `docs_status` —
  never a direct `tools.AgentCodingStandards(...)` call, which would not compile before T4.
  Add `agent_coding_standards` theory rows to `McpPackagingTests` (routing metadata,
  `ReadOnly`/`OpenWorld`, zero negative boundary — its rows and `McpToolMethod` are already
  reflection-based, so they compile unchanged), add the tool to
  `McpPackagingTests.ServerInstructionsTellModelsToTryDocsExploreBeforeFileSearch`'s
  enumeration (D15 — approved at the 2026-10-15 gate), and add the new tool's miss to
  `McpDocsProvenanceTests.AllResponses`
  and `MissAndEmptyResponsesStillLeadWithProvenance` — the sixth tool's entry is yielded
  with the same reflection+Invoke idiom the file already uses for `docs_status` (verified
  2026-10-15: `AllResponses` is a private helper whose existing entries call the five tool
  methods directly, so a direct sixth call would break compilation at this task's RED
  point). Record
  RED: the tool-existence reflection assert fails at runtime (absent tool).
- **T4 — GREEN: implement the tool** (`csharp-dev`). In `src/KyberWeave.Mcp/DocsTools.cs` add
  `AgentCodingStandards` per §3.1, §3.3, §3.4, reusing `Provenance`, `Lead`, `AppendIdentity`,
  `AppendExcerpt`, `CapToBudget`, `Truncate`, and `SectionCharCap`; add the rule-id extraction
  (pattern per D9) and the status warning. Pre-check whitespace `technology` input at the tool
  and answer usage prose without calling the engine — the engine folds an empty key into a
  miss indistinguishable from an unknown technology (D14). Also extend
  `src/KyberWeave.Mcp/DocsServerInstructions.cs` `Text` with the sixth tool (D15, approved
  at the 2026-10-15 gate); `Program.cs`
  stays untouched. No stdout writes; no new dependencies. Record GREEN per T3.
- **T5 — Squad roster registration (DEC-4, D16)** (`test-dev` + `csharp-dev`). RED: add the
  parity guard test (after T4) — executed in the new file
  `tests/KyberWeave.Tests/SquadToolchainRosterGuardTests.cs` (class
  `SquadToolchainRosterGuardTests`, test
  `ShippedToolchainRosterGrantsEveryToolTheKyberWeaveMcpServerExposes`; post-execution
  correction 2026-10-15, findings drain: the guard is server-versus-roster parity, not tool
  behavior, so it landed in its own file — executed fact, not a deviation) — asserting every
  `DocsTools` `[McpServerTool]` name appears in
  the shipped `products/kyber-squad/toolchain.yml` `required-mcp-tools.kyber-weave`, loaded
  via `SquadSourceLoader.Load(Path.Combine(KyberWeaveTestPaths.ToolRoot, "products",
  "kyber-squad"))`. On the merged tree the guard fails on two names: `agent_coding_standards`
  and the pre-existing `docs_status` gap (D16). GREEN: edit the roster to D16's answered
  outcome — add both `agent_coding_standards` and `docs_status` (2026-10-15 gate; stable
  order preserved; no carve-out) — and confirm
  `products/kyber-squad/mcp.json` needs no edit. **D16 answered 2026-10-15 — T5 is unblocked
  once T4 lands.**
- **T6 — Documentation (same-change contract)** (`docs-dev`). Update
  `docs/docgraph/mcp-runbook.md` per §3.6 — new tool section, "All five" → six, troubleshooting
  row. No `retrieval.md` change (D8). Verify with `docs validate .` and `docs drift .` — zero
  findings. Explicitly a no-unit-test task; the read-only verification is its contract.
- **T7 — Whole-change verification and review** (`csharp-dev` / `code-review`). Run the
  declared gate suite (`review gates . --out artifacts/gates.json` or the root-`AGENTS.md`
  list); run the review council; fix findings. Closeout per §7.

### Dependency graph and dispatch

```
T1 ──► T2 ──► T3 ──► T4 ──┬─► T5 ──► T7
                          └─► T6 ──► T7
```

- T1 → T2 → T3 → T4 is serial (D12). T1 and T3 share one test assembly and each RED/GREEN
  point must stay observable at its own task boundary: T1's direct-call engine tests break
  compilation until T2 lands (the RED recorded before T2 was exactly that compile break),
  so T3's tool-response tests are authored with the reflection+Invoke idiom — the tool
  method does not exist until T4, so reflection remains the only compiling form for its
  response tests — and T4's GREEN is T3's tests passing. D12's serial chain
  stands as a dependency fact, not a concurrency cap.
- T4 consumes T2's engine method and T3's RED evidence.
- T5 requires T4 (the parity RED must fail on the roster gap alone, not a missing tool).
- T6 requires T4 (documents the final contract) and declares no dependency on T5 — both are
  ready once T4 lands, so they may run in parallel or in either order.
- T7 is last and depends on all.

**No concurrency cap (D13).** The queue dispatches every ready task immediately, with no
limit on parallelism; the only constraints are the declared dependencies above and each
task's file/symbol scope. T1–T4 run serially because of the D12 dependency chain — a
dependency fact, not a cap. T5 ∥ T6 is an ordering observation (both become ready at T4 and
declare no dependency on each other), not a cap.

## 7. Risks, out-of-scope, verification, closeout

### 7.1 Risks

- **Routing-metadata scoring.** The `[Description]` must score Trigger clause 35 / Specific
  opening 15 / Trigger keywords 15 under the existing scorer, and carry no exclusion. T3 pins
  this; write the description per the runbook's rules from the start.
- **Empty rule-id index today.** Both current standards state no concrete `KW-*` ids, so early
  responses will carry `(this standard states no rule ids)`. Mitigated by D9's honest-empty
  design; the response still contains the standard's rule-id *policy* section, which is what
  tells the agent ids are permanent.
- **Exact-name roster coupling.** `toolchain.yml` and `squad doctor` match tool names exactly;
  a future rename breaks installs the way context7's rename did. T5's parity guard makes the
  mismatch a test failure rather than a deployed silent gap. The guard's first catch is
  already present on the merged tree: `docs_status` is missing from the shipped roster today
  (D16) — evidence the drift it exists to prevent is real; the user's 2026-10-15 answer
  fixes it (both names added to the roster, no carve-out) rather than silently absorbing it.
- **Name outside the `docs_` prefix.** Mitigated in DEC-2; the runbook roster keeps all tools
  in one list so no client relies on a prefix.

### 7.2 Verification gates

In-flight, while this plan is open in `docs/plans/` (T6, T7, and every intermediate
verification):

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

or the equivalent via `dotnet run --project src/KyberWeave.Cli --no-build -c Release --
review gates . --out artifacts/gates.json` — the declared suite's `docs-validate` gate runs
`docs validate .` without `--merge-ready` (`review.gates` in `.kyber-weave/kyber-weave.yml`),
so the gate suite is expected green while this plan is open.

Merge-ready is a closeout check, not an in-flight gate. Root `AGENTS.md` lists
`docs validate . --merge-ready` among its pre-PR commands, and PR CI runs that form
(`.github/workflows/ci.yml`, "Docs validate" step). `--merge-ready` adds
`KW-DOC-LIFECYCLE-003`, which fails on any plan still open in `docs/plans/` — by design:
open work is correct on a branch while it is built, never in a merge. A clean merge-ready
validation is therefore expected **only at closeout, after §7.4's archival** moves this plan
to `docs/archive/plans/` and updates `docs/plans/README.md`, inside the finishing PR before
it merges. Before that archival, `--merge-ready` reporting exactly one
`KW-DOC-LIFECYCLE-003` finding naming this plan is the expected in-flight state, not a
defect; T1–T7 must not treat it as a gate failure.

Dash gates do not apply (no `dash/` change); no distribution/self-updater surface is touched,
so the release loop is not required for this change (the squad release that ships the tool is
the normal release process, not this plan's gate).

**Validation status (Draft):** run by the conductor's worker on this Draft, 2026-10-14.
`docs validate . --merge-ready` exits 1 with exactly one finding — `KW-DOC-LIFECYCLE-003`
naming this plan, the expected in-flight state above (the open-work validator is the only
merge-ready addition, so plain `docs validate .` has zero findings). `docs drift .` exits 0
with zero findings; the CodeGraph index was present and usable. T6 and T7 re-run both
against the changed corpus regardless.

### 7.3 Out of scope

- No agent-name or squad-name lookup (DEC-1); `topic` is not that.
- No ontology widening: no new frontmatter key for rule ids, no new doc-type or status.
- No scoring changes; `docs/docgraph/retrieval.md` is untouched.
- No CodeGraph involvement: no joins, no `.codegraph/` reads.
- No edits to `docs/standards/*` content, `src/KyberWeave.Core/Docs/Search/DocumentIndexHost.cs`,
  or the MCP composition root (`Program.cs` unchanged).
- Squad agent instruction files (`products/kyber-squad/agents/**`) are not updated to invoke
  the tool: agents resolve standards by Config Reg path today, those files are `always-human`
  review paths, and prompt adoption is follow-up work to be proposed separately.
- KyberDash, the tray, and the web dashboard are untouched.

### 7.4 Review and docs-dev closeout

The four-lens whole-change review ran after T7 and returned APPROVE at the finding level. Its
one engine escalation, `KW-REVIEW-008` on the `always-human` `.kyber-weave/kyber-weave.yml`
path, was resolved by the user signing off the change rather than by a mechanical edit the
engine could accept.

**Closeout mapping, completed 2026-10-15.** Every durable decision in §4 has a named
destination: canonical documentation, the product file that carries it, or the test that
stands in for one.

| Decision | Durable content | Destination |
|---|---|---|
| DEC-1 | The lookup key is a technology slug or its Config Reg property name; no agent- or squad-name keying | [docgraph/mcp-runbook.md](../docgraph/mcp-runbook.md) — the `agent_coding_standards` tool section (key forms, and the corpus-decides rule) |
| DEC-2 | One read-only tool on `kyber-weave-mcp` named exactly `agent_coding_standards`, deliberately outside the `docs_` prefix | Same runbook section; the tool roster that lists it beside the `docs_*` tools |
| DEC-3 | The response carries a rule-id index of the `KW-*` ids the standard states, emitted ahead of the prose and honest when empty | Same runbook section, "The rule-id index is emitted ahead of the prose" |
| DEC-4 | Deployment scope: this repository's server **and** the Kyber-Squad roster `ZCodeRenderer` grants from and `squad doctor` checks | [products/kyber-squad/toolchain.yml](../../products/kyber-squad/toolchain.yml) `required-mcp-tools.kyber-weave`, plus `tests/KyberWeave.Tests/SquadToolchainRosterGuardTests.cs` |
| DEC-5 | `development-mode: test-first` | Frontmatter, §5's Test contract, and the per-task RED/GREEN evidence in §6 |
| D6 | The parameter set (`technology`, `topic?`, `charBudget`) and its bounds | Runbook tool section (parameters, the 1000–120000 clamp, the 12000 default) |
| D7 | Key normalization and corpus-side resolution | Runbook tool section (trim; one surrounding `<>` pair; the `-coding-standard` suffix; case-insensitive match; the corpus decides, not the registry) |
| D8 | Section selection reuses the documented rarity-weighted ranking; no new weights | Runbook tool section, cross-linked to [docgraph/retrieval.md](../docgraph/retrieval.md)'s "Budget, not truncation"; that page is unedited |
| D9 | Rule-id extraction mechanics: pattern over the document, dedupe in citation order, cap 50, honest empty | Runbook tool section (index order, the 50-id window, the withheld-id replacement) |
| D10 | Miss, empty-input, and non-`CURRENT`-status behavior | Runbook tool section and its troubleshooting table ("`agent_coding_standards` reports no standard for a technology") |
| D11 | Provenance whole, budget from the body, per-section cap, no tool-layer caching | Runbook tool section; the executed deviation from D11's whole-body-clip wording is §4's post-execution note and §3.3 |
| D12 | T1–T4 serialize over the shared test assembly; tool-response tests use the reflection+Invoke idiom | Execution record in §6; the shipped tests (`McpAgentCodingStandardsTests`, `McpDocsProvenanceTests`) are the enduring copy |
| D13 | No concurrency cap: dispatch follows declared dependencies and file scope only | Execution policy recorded in §6; no canonical document declares a cap, so nothing needed harvesting outward |
| D14 | Withdrawn — no surviving decision. The two facts it surfaced (T2's private `WholeExcerpt` realization of the design's `ExcerptInDocumentOrder`; the engine folding an empty key into a miss, so the tool pre-checks whitespace itself) | §2, §3.2, §3.4 and §6; the whitespace behavior is the runbook's empty-input usage prose |
| D15 | The server-instructions surface is a same-change edit, pinned in the same change | `src/KyberWeave.Mcp/DocsServerInstructions.cs` (`Text`) and its pinning assert in `tests/KyberWeave.Tests/McpPackagingTests.cs`; §3.6 |
| D16 | The shipped roster must list every `DocsTools` tool; both names added, no carve-out | [products/kyber-squad/toolchain.yml](../../products/kyber-squad/toolchain.yml) and the parity guard test; §3.6 |
| Withheld-rule-id elision (executed evidence) | The index caps at 50 ids and an id past the cap is replaced with `[rule id withheld]` everywhere the response echoes the document's own text | [docgraph/mcp-runbook.md](../docgraph/mcp-runbook.md) rule-id index section; the four pinning tests and their mutation evidence are §7.5 |
| Coverlet stale instrumentation (executed evidence) | An interrupted `--coverlet` run leaves `bin/Release` instrumented, and the `build` gate must therefore force a full rebuild | `.kyber-weave/kyber-weave.yml` `build` gate comment (`--no-incremental`), mirrored in `scripts/fast-loop.sh`; §7.5 |

**ADR disposition: no ADR — ADR waiver.** The tool refines the existing DocGraph MCP surface
under the established ontology and Config Reg pattern
([ADR 0001](../adr/0001-coding-standards-and-config-registry.md)). It adds one read-only
lookup over the in-memory corpus: no new architectural invariant, dependency or boundary, no
new score or threshold, no composition-root change. The two executed-evidence facts are
ADR-exempt for the same reason — the elision is a response-formatting rule inside an existing
tool contract, and the coverlet constraint is a property of the existing `review gates` build
gate.

**No documentation waiver is claimed.** Both executed-evidence facts carry surviving
operational invariants for a future engineer, and both are recorded in canonical documents —
the runbook and the gate configuration that declares the fix — not only here.

**Archival (finishing PR).** Move this file to
`docs/archive/plans/2026-10-14-mcp-agent-coding-standards-tool.md`, flip frontmatter
`status: current` → `status: complete`, and move its row out of the Active Plans table in
[`docs/plans/README.md`](README.md) into the Archived Plans table, per KW-DOC-LIFECYCLE-003.
The new row's canonical-docs cell is this section's mapping table.

### 7.5 Closeout verification (2026-10-15)

**Acceptance criteria (§8).** Each criterion was checked against the shipped tree rather than
against this plan's own prose:

| # | Verified by |
|---|---|
| 1 | `src/KyberWeave.Mcp/DocsTools.cs:422` declares `[McpServerTool(Name = "agent_coding_standards", ReadOnly = true, OpenWorld = false)]`; `tests/KyberWeave.Tests/McpPackagingTests.cs:79` and `:172` carry its theory rows, and `:158` pins its negative-boundary score at 0 |
| 2 | `McpAgentCodingStandardsTests.EveryResponseLeadsWithAWholeProvenanceLineForTheCorpusThatAnswered`; the hit path commits no whole-body re-clip — `src/KyberWeave.Mcp/DocsTools.cs:493` returns `Lead(provenance, sb.ToString())` — the budget being honoured at the edges instead: the clamp at `:465`, Core's 1500-character per-document floor, and per-section truncation at `SectionCharCap`. `ASectionOverTheCapIsCutAtSixThousandCharacters`, `ASectionDroppedForSpaceNamesWhatDidNotFitAndSuggestsABiggerBudget` and `ASectionDroppedForIrrelevanceIsNamedWithoutSuggestingABiggerBudget` pin the omission markers |
| 3 | `TheTechnologyLineEchoesTheValueTheCorpusDeclaresRatherThanTheKeyTheCallerTyped`, `AnUnknownTechnologyIsAnExplicitMissThatNamesWhatTheCorpusDeclares`, `AnEmptyOrWhitespaceTechnologyIsAUsageResponseNamingBothAcceptedKeyForms`; the resolution itself is `DocumentIndex.NormalizeTechnologyKey` (`src/KyberWeave.Core/Docs/Search/DocumentIndex.cs:357`) |
| 4 | `TheRuleIdIndexPrecedesTheSectionsSoABudgetCanNeverDropIt`, `TheRuleIdIndexDedupesAndKeepsTheOrderTheDocumentCitesThem`, `AnIndexOfMoreThanFiftyRuleIdsStopsAtFiftyAndSaysHowManyItDropped`, `AStandardThatStatesNoRuleIdsSaysSoRatherThanShippingAnEmptyIndex`, `AStandardWhoseStatusIsNotCurrentStillAnswersAndCarriesAWarningNamingTheStatus` |
| 5 | `Program.cs` and `docs/docgraph/retrieval.md` are unedited by this change; `Explore`/`ForSymbol` and every scoring constant are unmodified; no dependency was added |
| 6 | `products/kyber-squad/toolchain.yml:27-33` lists `agent_coding_standards` and `docs_status`; `SquadToolchainRosterGuardTests.ShippedToolchainRosterGrantsEveryToolTheKyberWeaveMcpServerExposes` is the guard; `products/kyber-squad/mcp.json` is unedited |
| 7 | `docs/docgraph/mcp-runbook.md:350` documents the tool and `:524` carries its troubleshooting row; the declared suite was 15/15 at T7; the `--merge-ready` check is the post-archival step (§7.2) |
| 8 | `src/KyberWeave.Mcp/DocsServerInstructions.cs:24` names the tool and `McpPackagingTests.cs:122` asserts it; `DocumentIndex.CodingStandards` / `CodingStandardTechnologies` are T2's implementation, with `CodingStandardsLookupTests` (`tests/KyberWeave.Tests/DocumentIndexTests.cs:579`) as their contract |

**Durable fact — the withheld-rule-id elision (executed evidence).** The response caps the
rule-id index at 50 ids, and an id past the cap is replaced with `[rule id withheld]`
everywhere the response echoes the document's own text: the headings and bodies of the
returned sections, the title echoed in the identity block, and the headings the omission
markers name. It is a replacement, never a deletion, so a reader sees that an id was cited
rather than that a sentence lost a word; consequently the cap the response states is a cap it
keeps, and an id it dropped cannot leak further down and contradict its own count. Four tests
pin the behaviour, each proven by removing the production behaviour it covers and observing
the test fail: `AnIndexOfMoreThanFiftyRuleIdsStopsAtFiftyAndSaysHowManyItDropped` (the cap and
its `… N more` remainder), `ATitleOnlyRuleIdPastTheCapIsCountedInTheRemainderAndWithheldFromTheTitleLine`
(the echoed title, where a title-only id lands after the sections and so is the id the cap
cuts), `AWithheldRuleIdIsNeverPrintedWhereverTheResponseEchoesTheDocument` (the returned
headings and bodies) and `AWithheldRuleIdIsNotPrintedInTheHeadingsNamedWhenTheTopicMatchesNoSection`
(the headings the omission markers name). Recorded here because the evidence lived only in
session scratch until closeout. Canonical destination: the runbook's rule-id index section,
which states the cap, the replacement and its exemption for the identity block's `path:`,
`id:` and `technology:` lines; the tests themselves are the enduring record.

**Durable fact — coverlet stale instrumentation (executed evidence).** The declared `test`
gate runs `dotnet test … --coverlet` (Microsoft Testing Platform's coverage extension
instruments `bin/Release` in place, rather than collecting out of process). An interrupted
coverage run therefore leaves the test assembly and the libraries it loads carrying the
instrumenting session's temp path baked in; an incremental build cannot tell those survivors
from fresh outputs, and five process-spawning MCP tests then die with SIGABRT. The fix, which
the user approved because `.kyber-weave/kyber-weave.yml` is an `always-human` path, adds
`--no-incremental` to the `build` gate so the `--no-build` test gate that follows always runs
against binaries that are executable, and mirrors it in `scripts/fast-loop.sh`. Canonical
destination: the `build` gate's own comment in `.kyber-weave/kyber-weave.yml` (lines 40–46),
which states the reason where the gate is declared.

**Session limitation.** This closeout session had no process-execution tool, so
`docs validate .`, `docs validate . --merge-ready` and `docs drift .` were not run here, and
the file relocation in §7.4 could not be performed. Both remain the finishing PR's steps; no
new finding was introduced by this pass.

## 8. Acceptance criteria

1. `kyber-weave-mcp` exposes `agent_coding_standards`, declared `ReadOnly = true,
   OpenWorld = false`, whose description scores as routing metadata under the pinned scorer
   and carries no exclusion.
2. Every response — hit, miss, and usage — leads with the whole provenance line; the body
   honors `charBudget` (1000–120000, default 12000) through the engine's excerpt selection,
   Core's 1500-character per-document floor, and the 6000-character per-section cap, with no
   whole-body re-clip (the assembled body is returned uncapped past those bounds, matching
   `Explore` and `ForSymbol`; post-execution correction 2026-10-15, findings drain);
   omissions are named with the space-vs-relevance distinction.
3. A technology resolves by slug, bare Config Reg property name, and angle-bracketed property
   name; a miss names the technologies the corpus declares and the count considered.
4. The response carries the rule-id index before the sections, deduped in document order,
   capped at 50, honestly empty when the standard states no ids; a non-current standard is
   returned with a status warning.
5. No new scoring, ontology, dependency, or composition-root change; `Explore`/`ForSymbol`
   behavior is unchanged; `docs/docgraph/retrieval.md` is untouched.
6. `products/kyber-squad/toolchain.yml` lists both `agent_coding_standards` and
   `docs_status` (D16's answered outcome, 2026-10-15 gate — no carve-out); the parity
   guard test passes with every `DocsTools` `[McpServerTool]` name present in the roster;
   `products/kyber-squad/mcp.json` is unchanged.
7. `docs/docgraph/mcp-runbook.md` documents the sixth tool; `docs validate .` and
   `docs drift .` report zero findings; the full gate suite is green; and, at closeout after
   §7.4's archival, `docs validate . --merge-ready` reports zero findings.
8. `src/KyberWeave.Mcp/DocsServerInstructions.cs` names `agent_coding_standards` in its
   "Also available" roster (D15), with `Program.cs` untouched; the engine query
   (`DocumentIndex.CodingStandards` / `CodingStandardTechnologies`, implemented in-session
   by T2 per §3.2 — D14's corrected row) satisfies T1's tests with no behavior change to
   `Explore`/`ForSymbol`.
