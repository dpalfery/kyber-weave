---
id: archive/specs/kyberdash/tasks
title: KyberDash implementation plan
doc-type: spec
status: archived
owner: dpalfery
last-reviewed: 2026-08-29
component: KyberDash
---

# Implementation Plan

Sequenced so that correctness lands before the surfaces that display it, and so that the
parity gate (task 8) can run as early as possible — it is what authorizes retiring the Python
pipeline, and everything after it is cheaper if it runs green sooner.

Task 1 is a spike and blocks task 12 only. Tasks 2 through 11 do not depend on it.

- [x] 1. Spike single-executable packaging
  - Build the upstream bundle unmodified, add a CommonJS output alongside the existing ESM one in the bundler config, and produce a single executable for one runtime identifier
  - Run the resulting binary against a real session corpus and confirm output matches the unbundled CLI
  - Repeat for all five supported runtime identifiers; record the outcome as a decision in `design.md`
  - If the CommonJS entry cannot load the terminal UI dependency chain, implement the pinned-runtime archive fallback instead and record why
  - _Requirements: 13.2, 13.3_

- [ ] 2. Land the subtree and integrate the repository
- [x] 2.1 Vendor upstream and establish the merge boundary
  - `git subtree add --prefix=dash` against upstream with squash; add the upstream remote
  - Extend `.gitignore` for build output under `dash/`, checking that the existing `**/bin/` rule does not swallow an upstream directory
  - Extend `.editorconfig` with TypeScript and Swift sections
  - Create `dash/kyber/` with a README stating the merge-zone rule, and leave the unshipped upstream surface directories untouched
  - Add a test asserting no KyberDash source file lives under `dash/src/`
  - _Requirements: 14.1, 14.2, 14.4_
- [x] 2.2 Wire the TypeScript gates into the declared suite
  - Add type-check, test and lint scripts to the workspace
  - Register all three as blocking gates in the repository gate configuration, as argv arrays
  - Add a Node job to the CI workflow running the same three
  - _Requirements: 14.1_
- [x] 2.3 Remove the superseded npm distribution path
  - Delete the in-tree npm wrapper directory and its four ignore exceptions
  - Update the distribution documentation to describe one install path
  - _Requirements: 13.1_

- [ ] 3. Canonical model and store
- [x] 3.1 Implement the canonical record and token validation
  - Create `dash/kyber/canon/types.ts` with the canonical record, `TokenUsage`, `CostBlock`, `Measurability`, `Problem` and the canonical content keys
  - Implement `validateTokens()` enforcing disjoint classes and the reported-input identity
  - Write table-driven unit tests covering valid decompositions, negative fresh input, and sums that do not reconcile — asserting both that valid records pass and that each invalid shape is rejected
  - _Requirements: 4.1, 4.3, 4.4_
- [x] 3.2 Implement the store
  - Create `dash/kyber/canon/store.ts` using the runtime's built-in SQLite module, with the schema as a version-controlled constant executed on construction
  - Implement idempotent upsert keyed on record identifier, the ingest log, quarantine and problem tables, and metadata carrying the schema version
  - Compress the raw column; write a round-trip test and a test asserting stored bytes per record stay under the budget
  - Write a test ingesting the same corpus twice and asserting no change
  - _Requirements: 2.5, 12.4_
- [x] 3.3 Implement tokenization with its cache
  - Create `dash/kyber/canon/tokens.ts` wrapping the tokenizer with the store-backed memo cache
  - Return counts tagged as derived, carrying the model name, so consumers can label them a lower bound
  - Write unit tests for cache hit and miss paths and for the derived tagging
  - _Requirements: 4.6_

- [ ] 4. Cost engine
- [x] 4.1 Implement cost blocks, bases and tier resolution
  - Create `dash/kyber/canon/cost.ts` producing a cost block that carries its basis
  - Prefer a harness-reported figure over a derived one; resolve context tiers by measured input size
  - Write unit tests asserting that figures of different bases are never summed into one total
  - _Requirements: 5.1, 5.2, 5.6_
- [x] 4.2 Implement rate-table scoping and the unpriced cases
  - Enforce the applicability list so a table prices only the harnesses it names
  - Render absent rates as no-published-rate and preserve the explicitly-not-billed case as distinct
  - Write a regression test reproducing the two-harnesses-one-model case, asserting the out-of-scope harness is not priced by the table
  - _Requirements: 5.3, 5.4, 5.5_

- [ ] 5. Harness adapters and attribution
- [x] 5.1 Implement the adapter interface and two-pass attribution
  - Create `dash/kyber/canon/adapters/base.ts` and `registry.ts` with detect, relevance, normalize, group, root resolution, validate and a method declaring what the harness does not export
  - Implement fingerprint scoring with a confidence threshold, then source inheritance for undecided records
  - Write unit tests asserting attribution never derives from the telemetry source name, including the case of one harness appearing under several suffixed sources
  - _Requirements: 6.2_
- [x] 5.2 Implement quarantine and problem capture
  - Quarantine unclaimed records with their observed attribute namespaces; never infer a harness
  - Persist problems from validation failures with severity, code and location
  - Write unit tests asserting a record from an unmodelled harness quarantines rather than mis-attributing
  - _Requirements: 6.1, 6.3, 6.4_
- [x] 5.3 Port the three harness adapters
  - Port the Copilot, pi and Gemini adapters into `dash/kyber/canon/adapters/`, converting each harness's token convention on the way in
  - Implement per-request reconciliation exposing a match indicator
  - Write per-adapter tests asserting both that the convention is applied correctly and that validation catches it being applied wrongly — the inverted-convention case must fail loudly
  - _Requirements: 4.2, 4.5_

- [ ] 6. OTLP receiver
- [x] 6.1 Implement the HTTP listener and both encodings
  - Create `dash/kyber/otel/receiver.ts` serving the trace endpoint on the standard port
  - Decode both JSON and protobuf payloads into the common record shape
  - Write integration tests posting a fixture in each encoding and asserting identical stored records
  - _Requirements: 2.1, 2.2, 2.3_
- [x] 6.2 Implement startup diagnostics and write batching
  - Report a bound-port conflict with the occupying process where discoverable; never silently rebind
  - Batch writes with backpressure so no record is dropped under load
  - Write tests for the port-conflict diagnostic and for a burst exceeding write throughput
  - _Requirements: 2.4, 2.5_
- [x] 6.3 Implement the optional Aspire source
  - Create `dash/kyber/otel/aspire.ts` reading spans exported from a running Aspire dashboard, supervised with backoff
  - Write a test over a fixture export asserting records land identically to the receiver path
  - Write a test asserting that records whose parent is missing are still grouped, by attribute rather than ancestry
  - _Requirements: 2.6, 2.7_

- [ ] 7. Port the analyses
- [x] 7.1 Context composition and pressure
  - Create `dash/kyber/analysis/context.ts` bucketing input by part type, never by message role, with instruction-block stripping and MCP server resolution
  - Compute headroom, accumulation rate, pressure per turn, and flag sharp fresh-input rises
  - Expose the unbucketed residual explicitly
  - Write unit tests for each bucket, for the residual, and for the flagged-jump threshold
  - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5_
- [x] 7.2 Tool and schema cost
  - Create `dash/kyber/analysis/schema.ts` ranking definitions by schema tokens multiplied by turns resident, reporting never-invoked cost separately and grouping by MCP server against ground-truth names
  - Express unused-schema cost as a range bounded by the cache-read floor and fresh-input ceiling
  - Write unit tests for ranking, grouping, and the bounded range
  - _Requirements: 8.1, 8.2, 8.3, 8.4_
- [x] 7.3 Execution structure
  - Create `dash/kyber/analysis/timeline.ts` building the hierarchical call tree with attribute inspection payloads
  - Identify subagent sessions and their parent; separate auxiliary activity while still reporting its spend
  - Write unit tests over a fixture trace containing a subagent and an auxiliary turn
  - _Requirements: 9.1, 9.2, 9.3, 9.4_
- [x] 7.4 Cross-harness comparison
  - Create `dash/kyber/analysis/compare.ts` producing the metric table with per-metric availability independent of value
  - Lead with per-turn ratios; compare cost only through a declared basis
  - Write unit tests asserting a metric a harness cannot report renders unavailable, never zero
  - _Requirements: 10.1, 10.3, 10.4_

- [ ] 8. Parity gate
- [x] 8.1 Build the parity harness
  - Create `dash/kyber/tools/parity.ts` running the ported pipeline over a span corpus and emitting the same content-free digest shape as the Python pipeline
  - Add a test that fails when the digests differ, and that reports which section diverged
  - _Requirements: 15.1, 15.2_
- [x] 8.2 Implement clean re-ingest
  - Implement ingest from existing span exports so the corpus is reconstructible without migrating the old derived store
  - Write a test asserting a fresh store built from exports reaches the same digest
  - _Requirements: 15.3_

- [ ] 9. Unified ingest
- [x] 9.1 Implement the span synthesizer
  - Create `dash/kyber/synth/` consuming upstream's parsed-call output and emitting canonical records, converting each provider's token convention on the way in
  - Preserve upstream's parallel cold-parse path; write a test asserting parallel and serial produce identical output
  - _Requirements: 1.1, 1.4, 1.5_
- [x] 9.2 Implement provider failure handling
  - Omit absent provider stores silently; record a problem naming provider and file for an unparseable store and continue with every other provider
  - Write tests for the absent case and the corrupt-file case, asserting the corrupt case does not abort the run
  - _Requirements: 1.2, 1.3_
- [x] 9.3 Implement cross-path deduplication
  - Extend upstream's existing deduplication key rather than adding a second mechanism, so a session seen through both paths resolves to one identity
  - Prefer the richer source on disagreement and record the disagreement as a problem
  - Write an integration test ingesting one session through both paths and asserting turns, tokens and cost are counted once
  - _Requirements: 3.1, 3.2, 3.3_
- [x] 9.4 Implement measurability declarations
  - Declare per-source measurability for each metric and propagate it through the analyses
  - Write tests asserting a file-sourced provider with no tool definitions reports schema ranking and context composition as not measurable rather than zero
  - _Requirements: 7.6, 8.5, 10.2_

- [ ] 10. Surfaces
- [x] 10.1 Unify the data path behind the terminal dashboard
  - Route the terminal dashboard's period reports, breakdown tables and daily activity through the canonical store so file-sourced and OTLP-sourced sessions appear together
  - Write a test asserting the terminal output covers a corpus containing both
  - _Requirements: 11.1, 11.2_
- [x] 10.2 Extend the web dashboard
  - Add views for context composition and the heatmap, schema cost ranking, the trace timeline, comparison, and the quarantine and problems views
  - Render the derived-token caveat with its model name wherever derived counts are shown
  - Write component tests for the unavailable-metric rendering path
  - _Requirements: 11.3_
- [x] 10.3 Extend the status contract
  - Extend the machine-readable status format to carry the new analyses; record this file in `design.md` as a deliberate merge-zone edit with its reason
  - Update the menu-bar and Electron clients only where the binary name changes
  - Pin the contract with a test, so a change that would break the native clients fails in CI
  - _Requirements: 11.4, 11.5, 14.3_
- [x] 10.4 Expose the analyses over MCP
  - Extend the MCP server so an agent can retrieve the same figures
  - Write a test asserting the MCP payload and the status payload agree
  - _Requirements: 11.6_

- [x] 11. Data-handling guarantees
  - Write a test asserting no request carries session or span content off the machine, and that the pricing fetch sends no user data and falls back to cache on failure
  - Write a test asserting tracked files carry no captured content, seeded from local-only markers
  - _Requirements: 12.1, 12.2, 12.3_

- [ ] 12. Release
- [x] 12.1 Produce and publish verified binaries
  - Implement the packaging recipe from task 1 in the release workflow for all five runtime identifiers, with checksums published alongside the existing binaries
  - Extend the installer to place the new artifact, requiring no package manager, runtime or elevated privileges
  - Add a test covering checksum verification and the platform-identifier resolution
  - _Requirements: 13.1, 13.2, 13.3_
- [x] 12.2 Wire the signed menu-bar install
  - Implement the command that downloads, verifies and installs the signed macOS application
  - Write a test asserting installation is refused when verification fails
  - _Requirements: 13.4_

- [x] 13. Specification closeout
  - Assign to `docs-dev`. Verify every requirement against implementation evidence, migrate the specification's durable content into canonical documentation — in particular the measured rationale behind requirements 4, 5 and 6, which must survive the retirement of the Python project — update the specification index and the component catalog, then archive `kyberdash/`.
  - _Requirements: all, 15.4_
