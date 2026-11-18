---
id: specs/interface-driven-unit-tests/inventory/domain-move
title: Inventory of Domain's data and logic, and the callers of its logic
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
component: CI Pipelines
---

# Inventory of Domain's data and logic, and the callers of its logic

Produced 2026-11-17 for task 2.1 of the [interface-driven unit tests specification](../README.md) on
branch `opencode/idt/domain-plan`, by a script run over every `.cs` file under `src/KyberWeave.Domain`, followed by a
read of 20 classifications. The tree scanned is the one this file was produced from; the last commit
that changed `src/KyberWeave.Domain` is `cead1588`. Evidence for the specification, not living
guidance: it goes stale as the move tasks land.

Owner decision D7 sets the end state: `KyberWeave.Domain` holds data only (records, DTOs, property
bags, value objects, enums, options, constants) and all logic moves to `KyberWeave.Application`.

**445 files, not 440.** The design and requirements say 440. The tree holds 445 at
`a5af901f` — the commit the other inventories were written at — and 445 today, so 440 is a
counting slip in the design, not a change since. This inventory counts 445 and classifies
every one of them.

Revised 2026-11-18: part 2 now routes every `Data` and `Core` caller row by the class of the Domain
type it names, instead of sending it to a `Contracts` interface by default. That default contradicted
requirement 4.3, which moves a pure function over its arguments to `Application` as a static helper —
a static cannot sit behind an interface — and requirement 3.1, which leaves an adapter no decisions
to hold.

## Bottom line

- **445 `.cs` files**: **155** stay (data), **285** hold logic and move,
  **5** hold nothing but interface declarations and move to `Contracts`.
  322 logic types are declared in those 285 files. 7 of the
  155 data files also declare an interface: the records stay and only the interface is
  lifted to `Contracts`, so those files are never moved whole.
- **Namespaces do not change.** Domain declares its types under `KyberWeave.Core` (350 files),
  `KyberWeave.Domain` (48), `KyberWeave.Arbiter` (29), `KyberWeave.Cli` (17) and `KyberWeave.Mcp` (1),
  and `Application` already carries files in those namespaces (25 in `KyberWeave.Application.Review`,
  14 in `KyberWeave.Core.Docs.Validation`, 9 in `KyberWeave.Mcp`), so a moved class keeps its namespace.
  A project that already references `Application` needs no source change at its call sites.
- **Three projects cannot reach the moved code**: `Data` (because `Application` references it),
  `Contracts` (a reference back would be a cycle), and `Core` (task 2.2 removes its `Application`
  reference). 65 files in `Data` and 13 in `Core` name a Domain logic type and take a route below.
  The first version of this part said 14 in `Core`; its extra row, `Core/Review/ReviewCycles.cs`, was
  deleted in `b9454059` before the tree this file was written from, so it is not a caller.
  `Core.Docs` has no caller of Domain logic; it has four files of its own to delete, listed in part 2.
  `Cli` (76 files), `Mcp` (4), `Arbiter` (2) and `Hooks` (1) reference
  `Application` and are unaffected.
- **The 81 Domain logic types those 78 rows name fall into three classes**: 20 data with a method (a),
  53 pure static helpers (b), and 8 stateful services (c). The class decides the route: 23 `Data` rows
  move to `Application`, 35 split (the raw-I/O adapter stays in `Data` and the logic moves), and 7 need
  no file move at all — 5 name data only, and 2 register or take the one `Contracts` interface that
  replaces a moved member. In `Core`, 7 rows move, 1 splits, 1 takes a `Contracts` interface, and 4 are
  deleted by tasks 2.2 and 2.3.
- **The order is not free.** The moves compile in any order only for callers that reference
  `Application`; `Data` and `Contracts` do not. Part 3 therefore resolves the 36 `Contracts` findings
  (step 0a) and splits the data-with-method types and reroutes the callers (step 0b) before any folder
  moves, which are then mechanical.
- **22 Domain logic types are named by 36 files in `Contracts`**: the finding the
  task asks for, with a decision per type in part 2, taken first in part 3's order.
- **24 move tasks**, one per Domain folder or group of subfolders, none over 30 logic classes.
  `Security` (2.5.10) is the pilot, and `Configuration` (2.5.1) waits for task 2.3.

## How a file was classified

The rule the scan implements:

- **data (stays)** — every type in the file has only properties, fields, constructors (including a
  constructor's validation and assignment of its own values), equality, `ToString`, deconstruction,
  comparison of itself, constants, and factory or `With*` copy helpers that only build another
  instance of the same type. A type with no body (`record X(...);`) is data. A data file may also
  declare an interface: only the interface leaves, for `Contracts`.
- **logic (moves to `Application`)** — any other method: a parser, renderer, validator, resolver,
  service, or a helper computing over other values. A factory that does more than construct, a
  `With*` helper that is not a copy, and every method named `Parse`, `Load`, `Render`, `Resolve`,
  `Validate`, `Find` or `Is*` is logic.
- **interface-only (the whole file moves to `Contracts`)** — every type it declares is an `interface`.
  It declares no logic, but D7's list of what Domain keeps does not include interfaces, and `Contracts`
  already references `Domain` for the records those interfaces name.
- The scan is a C#-shaped parser, not Roslyn: it strips comments and string literals, finds every type
  declaration with its body, then classifies each top-level member by its declaration header. It was
  checked against the compiler's own view by counting declarations: 968 types across 445 files.

### The 20 classifications read by hand

Four reads changed a verdict, and three more exposed a wrong reason behind a right verdict; each
changed the rule, and the table shows the verdict after the change, which is what the counts below
use.


| file | scan | reader | verdict |
|---|---|---|---|
| Arbiter/Facts/RefuteFinding.cs | logic | logic | agrees: a YAML-block reader, a parser |
| Arbiter/Sections/DocumentSection.cs | data | data | agrees: a positional record |
| Docs/Analysis/DocumentationAnalysisRuleIds.cs | data | data | agrees: constants only |
| Docs/Validation/PlanPacketContext.cs | logic | logic | agrees: records plus a plan parser |
| Arbiter/Credentials/KeychainUnavailableException.cs | data | data | disagreed: read `method base` from a constructor’s `: base(...)`; a constructor is data |
| Arbiter/Facts/CommentSyntax.cs | logic | logic | disagreed: read a field initializer’s `new(...)` as a method |
| Squad/Rendering/SquadCollectionExtensions.cs | logic | logic | disagreed: an extension method was read as data |
| Docs/Validation/TodoInventoryValidator.cs | logic | logic | disagreed: `Validate(DocumentSet)` was read as own-value validation |
| Squad/Model/CopilotToolCatalog.cs | logic | logic | disagreed: `Normalize` was in the trivial list |
| Configuration/ConfigRegConfig.cs | logic | logic | disagreed: read `method readonly` from a tuple-typed field |
| Squad/Briefs/BriefFacts.cs | logic | logic | disagreed: read `method static` from a tuple return type |
| Docs/Graph/DocGraphNode.cs | data | data | agrees: constructor validation, equality, properties |
| Review/Commands/ReviewOutcomeCodes.cs | data | data | agrees: rule-id constants |
| Hooks/HooksDefaults.cs | data | data | agrees: a shipped configuration bag |
| StatusLine/StatusLineDeploymentPlan.cs | data | data | agrees: a property bag, though its constructor projects two lists |
| Squad/Presets/SquadModelPreset.cs | logic | logic | agrees: `ApplyTo` merges profiles |
| Configuration/WorkerRosters.cs | logic | logic | borderline: `IsKnown` is a lookup over its own constants; kept logic, the rule errs toward moving |
| Skills/Model/SkillSet.cs | data | data | agrees: a list wrapper |
| Review/TaskGateRecords.cs | data | data | agrees: records and a callback holder |
| Arbiter/Hooks/Adapters/CopilotHookAdapter.cs | logic | logic | agrees: a hook adapter with a decision engine |

Rules learned from those reads: a constructor, its `: base(...)` initializer and its validation are
data; a tuple return type or tuple-typed field must not be read as a method; `Normalize` and
`Validate(...)` are logic, not own-value bookkeeping; an extension method is logic.

## Part 1: every file, by folder

Counts are files, then the logic types declared in them. "Interface-only" files hold declarations only.


| folder | files | data | logic | interface-only | logic types |
|---|---|---|---|---|---|
| Arbiter | 102 | 27 | 73 | 2 | 85 |
| Squad | 77 | 22 | 54 | 1 | 69 |
| Docs | 72 | 30 | 41 | 1 | 45 |
| Review | 50 | 14 | 36 | 0 | 42 |
| Configuration | 37 | 14 | 23 | 0 | 23 |
| Skills | 18 | 7 | 11 | 0 | 11 |
| Hooks | 17 | 10 | 7 | 0 | 7 |
| StatusLine | 16 | 9 | 7 | 0 | 7 |
| Doctor | 15 | 9 | 6 | 0 | 6 |
| Agents | 13 | 4 | 8 | 1 | 8 |
| RunStart | 9 | 3 | 6 | 0 | 6 |
| Update | 9 | 3 | 6 | 0 | 6 |
| CopilotPrompt | 4 | 0 | 4 | 0 | 4 |
| Security | 3 | 1 | 2 | 0 | 2 |
| CodeGraph | 2 | 2 | 0 | 0 | 0 |
| FileSystem | 1 | 0 | 1 | 0 | 1 |
| total | 445 | 155 | 285 | 5 | 322 |

### Arbiter

**Logic: 73 files, 85 types.** `AntigravityHookAdapter`, `ArbiterAnnotationNote`, `ArbiterAnswerText`, `ArbiterCliText`, `ArbiterConfigLoader`, `ArbiterDecisionQueries`, `ArbiterEscalationEnvelope`, `ArbiterFactSet`, `ArbiterFailureNotices`, `ArbiterFindingNotices`, `ArbiterGlobalHookPaths`, `ArbiterHookTimeout`, `ArbiterJson`, `ArbiterKeyResolver`, `ArbiterLedgerQueries`, `ArbiterModelTargets`, `ArbiterPathRule`, `ArbiterProvenance`, `ArbiterRecordId`, `ArbiterRule`, `ArbiterRuleScopes`, `ArbiterRunScope`, `ArbiterSkipNote`, `ArbiterSquadCatalog`, `ArbiterTriggerCatalog`, `ArbiterTriggerFamilies`, `ArbiterUserSettings`, `ArbiterUtcTimestampConverter`, `ArbiterVerifiedNote`, `ArchitectShellGuard`, `CallerResolver`, `ClaudeHookAdapter`, `CodexHookAdapter`, `CommandHookAdapters`, `CommentSummaryTable`, `CommentSyntax`, `ConductorGuardRules`, `CopilotHookAdapter`, `CursorHookAdapter`, `DevinHookAdapter`, `DispatchHeaders`, `DispatchPromptInsertion`, `DocumentSectionIndex`, `FactoryHookAdapter`, `FactoryTranscript`, `FailureFacts`, `GitDiffParser`, `GovernedPathsFacts`, `HarnessAdapterRegistry`, `HeaderBlock`, `HookFastPath`, `HookSnapshotDocument`, `LensInstructions`, `ModelTargetResolver`, `ModelTargetingRules`, `ModelTokenAliases`, `NoRedEvidence`, `NoneProvider`, `PlanDocument`, `PlanDocumentParser`, `PlanReadInstruction`, `PluginEnvelope`, `PluginHookAdapter`, `PluginHookAdapters`, `ProviderFailures`, `ReadGuard`, `RefutationOutcome`, `RefuteFinding`, `ReturnMarkerParser`, `ReturnedFinding`, `RuleEngine`, `RuleOperand`, `RulePredicate`, `RuleValidator`, `RunWriterProfiles`, `SecondaryTriggers`, `SectionSelector`, `StateBudget`, `SubstituteDigests`, `SystemOneContracts`, `TextExcerpts`, `TriggerClassifier`, `UnavailableDecisionEngine`, `Window`, `WorkerGuardRules`

**Data, stays (27 files):** `ArbiterConfig.cs`, `ArbiterEvaluationResult.cs`, `ArbiterEvaluatorOptions.cs`, `ArbiterOutcome.cs`, `ArbiterRunStartRemedy.cs`, `ArbiterTextLimits.cs`, `CommentTriageTypes.cs`, `Credentials/KeychainUnavailableException.cs`, `Credentials/SecretServiceUnavailableException.cs`, `Facts/ArbiterDecisionRecord.cs`, `Facts/ArbiterEvent.cs`, `Facts/ArbiterLogRead.cs`, `Facts/ArbiterModelBlock.cs`, `Facts/ArbiterModelTargetRecords.cs`, `Facts/FileExcerpt.cs`, `Facts/GitRecords.cs`, `HarnessGatingReport.cs`, `Hooks/ArbiterServeContext.cs`, `Hooks/ArbiterServeToken.cs`, `Hooks/BackgroundDispatch.cs` (interface `IBackgroundDispatchEngine` to lift), `Hooks/DispatchClaimOwners.cs`, `Hooks/HostConfigLoadResult.cs`, `Hooks/IHarnessHookAdapter.cs` (interface `IHookDecisionEngine, IHarnessHookAdapter` to lift), `ModelTargets/ArbiterSemanticProfiles.cs`, `Providers/IArbiterProvider.cs` (interface `IArbiterProvider` to lift), `Sections/DocumentSection.cs`, `Stores/ArbiterStoreTypes.cs`

**Interface-only, the whole file moves to `Contracts` (2 files):** `Credentials/ICredentialStore.cs`, `Hooks/IContextualHookDecisionEngine.cs`


### Squad

**Logic: 54 files, 69 types.** `AntigravityRenderer`, `ArbiterHookWiring`, `ArbiterModelTargetsFile`, `ArbiterPluginShim`, `BriefAssembler`, `BriefCondition`, `BriefFacts`, `BriefFormatter`, `BriefMap`, `CapabilityDegradations`, `ClaudeRenderer`, `ClaudeToolsFlowSequenceConverter`, `CodexRenderer`, `CommandCodeRenderer`, `CommandCodeYamlFlowSequenceConverter`, `CopilotAgentsFlowSequenceConverter`, `CopilotRenderer`, `CopilotToolCatalog`, `CopilotToolsFlowSequenceConverter`, `CursorRenderer`, `DevinRenderer`, `FactoryRenderer`, `FactoryYamlFlowSequenceConverter`, `KiloRenderer`, `MarkdownSections`, `McpPlan`, `MemoizedSquadGlobalRoots`, `OpenCodeRenderer`, `PiExtensionsFlowSequenceConverter`, `PiRenderer`, `SquadAgentResourceDiscoveryGuard`, `SquadCollectionExtensions`, `SquadDeploymentPlan`, `SquadGlobalRoots`, `SquadHarnessLowering`, `SquadHarnessRenderContext`, `SquadHarnessRenderer`, `SquadHarnessSpec`, `SquadHookJsonBlock`, `SquadInstanceCatalog`, `SquadInstanceRootResolver`, `SquadMarkdownDocument`, `SquadModelDiff`, `SquadModelPreset`, `SquadModelResolution`, `SquadOperatorChannel`, `SquadPathPolicy`, `SquadPhysicalRootIdentity`, `SquadReferenceLocator`, `SquadRenderRequest`, `SquadRenderSource`, `SquadResourceLinkRewriter`, `SquadResourceProjection`, `SquadResponseGuidanceSource`, `SquadResponsePreferencesInjector`, `SquadRetiredSkills`, `SquadSharedIdentityProjection`, `SquadSkillOutputPaths`, `SquadSourceValidationException`, `SquadSourceValidator`, `SquadTargetCatalog`, `SquadToolMap`, `SquadWorkerBaselineInjector`, `SquadYamlEmitter`, `WarpRenderer`, `ZCodeRenderer`

**Data, stays (22 files):** `Briefs/BriefModels.cs`, `Briefs/SquadBriefRequest.cs`, `Deployment/DevinImportOverlapReport.cs`, `Deployment/FactoryHooksShadowingModels.cs`, `Deployment/SquadDeploymentExceptions.cs`, `Deployment/SquadDeploymentModels.cs` (interface `ISquadUserPaths` to lift), `Deployment/SquadShippedGuidanceUnavailableException.cs`, `Deployment/SquadSkillShadowRecords.cs`, `Deployment/SquadTargetResolutionModels.cs`, `Deployment/SquadTransactionObservers.cs` (interface `ISquadTransactionObserver, ISquadTransactionCheckpointObserver` to lift), `Deployment/ZCodeMcpConfigurationReport.cs`, `Instances/SquadInstanceEnsureResult.cs`, `Instances/SquadInstanceLinkSyncResult.cs`, `Instances/SquadLinkException.cs`, `Instances/SquadLinkRecord.cs`, `Model/SquadSource.cs`, `Packaging/SquadPackModels.cs`, `Presets/SquadCanonicalProfiles.cs`, `Presets/SquadModelPresetSelection.cs`, `Release/SquadReleaseModels.cs`, `Release/ToolProbeResult.cs`, `Rendering/SquadOperatorChannelOverlay.cs`

**Interface-only, the whole file moves to `Contracts` (1 file):** `Deployment/IFileSystemPathSemantics.cs`


### Docs

**Logic: 41 files, 45 types.** `AnalysisCandidateId`, `AnalysisGlossary`, `ClaimExtractor`, `ConfigRegMarkdown`, `ContainerScan`, `CorpusProvenance`, `DocGraphProjection`, `DocSpecValidator`, `DocsScaffoldTemplates`, `DocumentCorpus`, `DocumentMarkdown`, `DocumentModel`, `DocumentSectionGraph`, `DocumentSectionTable`, `DocumentSet`, `EmbeddingCandidateBuilder`, `EmbeddingUsage`, `GhJson`, `HarnessMcpCatalog`, `HarnessMcpEntry`, `HostConfigLines`, `HostConfigYaml`, `IgnoreMarkupReader`, `IndexSectionCatalog`, `InventoryReachability`, `JsonServerSplice`, `KyberStandardsTemplates`, `LexicalSimilarity`, `McpConfigurationLink`, `McpDeclarationBlockWriter`, `McpRepositoryPath`, `McpWiringReceipt`, `PacketSelfContainment`, `PlanInventoryValidator`, `PlanPacketContext`, `PlanPacketInliner`, `PlanTaskFileFormat`, `PlanTaskFileRules`, `PlanTaskPacket`, `RunEvidence`, `ScaffoldValues`, `SectionFrontmatterParser`, `TaskFileCommand`, `TodoInventoryValidator`, `TomlTableScanner`

**Data, stays (30 files):** `Analysis/Candidates/CandidateModels.cs`, `Analysis/Claims/Claim.cs`, `Analysis/Claims/ClaimExtractionResult.cs`, `Analysis/DocumentationAnalysisRuleIds.cs`, `Analysis/Glossary/GlossaryModels.cs`, `Analysis/Persistence/PersistenceModels.cs`, `Analysis/Review/ReviewModels.cs`, `Closeout/PlanLifecycleOutcomes.cs`, `Closeout/PlanPathResolution.cs`, `Delivery/DeliveryRecords.cs`, `Delivery/PullRequestContent.cs`, `Export/DocGraphExportResult.cs`, `Graph/DocGraphContribution.cs`, `Graph/DocGraphEdge.cs`, `Graph/DocGraphNode.cs`, `Mcp/McpServerRenameModels.cs`, `Mcp/McpWiringFix.cs`, `Mcp/McpWiringModels.cs`, `Mcp/McpWiringRepairModels.cs`, `Model/DocType.cs`, `Parsing/ArchivedEntryRefusalException.cs`, `Scaffolding/ScaffoldModels.cs`, `Search/CodeJoin.cs`, `Search/DocumentExcerpt.cs`, `Search/DocumentHit.cs`, `Search/ExploreAnswer.cs`, `Validation/PlanTaskAgentPicker.cs` (interface `IPlanTaskAgentPicker` to lift), `Validation/PlanTaskFile.cs`, `Validation/PlanTaskFileOutcome.cs`, `Validation/PlanTaskTargetMatch.cs`

**Interface-only, the whole file moves to `Contracts` (1 file):** `IDocsEngineConfig.cs`


### Review

**Logic: 36 files, 42 types.** `CitedLineCheck`, `CoberturaReport`, `CouncilGateSelection`, `CouncilSeating`, `DuplicateDetector`, `DuplicateReport`, `FieldBuilder`, `FindingsReport`, `GateCatalogueRenderer`, `GitChangeParsing`, `ItemBuilder`, `NotSeatedRecord`, `ProjectRootDetection`, `RedClassifier`, `RedLanguages`, `RemovedAgentMap`, `ReviewCommandResult`, `ReviewCyclePlanning`, `ReviewDecisionCacheKey`, `ReviewDecisionText`, `ReviewEvidence`, `ReviewGateReporting`, `ReviewJson`, `ReviewLensRules`, `ReviewLoop`, `ReviewScope`, `ReviewSeatInput`, `ReviewVerdictLabel`, `ReviewVerdictRules`, `ReviewWorkTree`, `SeatFlatYamlReader`, `SeatOutputParser`, `SeverityOverrides`, `SuppressionCandidates`, `SuppressionScan`, `TaskGateNextStep`, `TaskGatePlanning`, `TaskSlug`, `TaskTestDigest`, `TechnologyAgentMap`, `TestClassPlanning`, `VerdictEngine`

**Data, stays (14 files):** `Catalogue/GateCatalogueFamily.cs`, `Catalogue/ProjectRoot.cs`, `Catalogue/RenderedReviewBlock.cs`, `Commands/ReviewOutcomeCodes.cs`, `RepositoryRootAccess.cs`, `ReviewDecisionRecords.cs`, `ReviewLensCatalogue.cs`, `ReviewOutcome.cs`, `ReviewTaskChoice.cs`, `ReviewVerdictRecords.cs`, `ReviewVerdictRequest.cs`, `ReviewVerdictResult.cs`, `SkippedTaskGate.cs`, `TaskGateRecords.cs`


### Configuration

**Logic: 23 files, 23 types.** `ConfigRegConfig`, `ConfigRegConfigLoader`, `ConfigSlug`, `DocsAnalysisConfig`, `DocsAnalysisConfigLoader`, `DocsLayout`, `FullSuiteGate`, `HarnessProfileConfig`, `HarnessProfileConfigLoader`, `KyberWeaveConfigAssembler`, `KyberWeaveConfigLoadResult`, `KyberWeaveYamlParser`, `McpConfigLoader`, `OntologyConfig`, `OntologyConfigLoadResult`, `OntologyConfigLoader`, `RequiredMcpServer`, `ReviewConfigParser`, `ReviewPolicy`, `ReviewTaskGateMigration`, `SquadConfigLoader`, `UserHostConfigPath`, `WorkerRosters`

**Data, stays (14 files):** `ArbiterYamlSection.cs`, `CatalogYamlSection.cs`, `DocsAnalysisYamlSection.cs`, `HarnessProfileYamlSection.cs`, `HarnessYamlSection.cs`, `KyberWeaveConfig.cs`, `KyberWeaveYamlDocument.cs`, `McpServerNames.cs`, `McpYamlSection.cs`, `OntologyYamlSection.cs`, `ReviewWorkingDirectoryFacts.cs`, `ReviewYamlSection.cs`, `SquadConfig.cs`, `SquadYamlSection.cs`


### Skills

**Logic: 11 files, 11 types.** `DescriptionScorer`, `FailOnPolicy`, `LexicalRoutingStrategy`, `RoutingEvaluator`, `RoutingLinter`, `SkillCatalogRow`, `SkillParser`, `SkillReviewExchange`, `SkillScaffold`, `SkillScanner`, `SpecValidator`

**Data, stays (7 files):** `Model/Skill.cs`, `Model/SkillFrontmatter.cs`, `Model/SkillSet.cs`, `Parsing/SkillLoadResult.cs`, `Parsing/SkillParseException.cs`, `Review/SkillReviewModels.cs`, `Routing/IRoutingStrategy.cs` (interface `IRoutingStrategy` to lift)


### Hooks

**Logic: 7 files, 7 types.** `ClaudeHookEventParser`, `HookFeedbackFormatter`, `HookParseResult`, `HookRouter`, `HooksConfigMerger`, `HooksConfigPath`, `PathContainment`

**Data, stays (10 files):** `HookEditEvent.cs`, `HookResult.cs`, `HookRule.cs`, `HookRuleKind.cs`, `HooksConfig.cs`, `HooksConfigOverlay.cs`, `HooksDefaults.cs`, `PlannedCommand.cs`, `ProjectFacts.cs`, `RuleOutcome.cs`


### StatusLine

**Logic: 7 files, 7 types.** `StatusLineReceiptFormat`, `StatusLineTargetRoots`, `UtilitiesStatusLineActivationGuidance`, `UtilitiesStatusLineArtifacts`, `UtilitiesStatusLineHarnessProbe`, `UtilitiesStatusLinePrerequisites`, `UtilitiesStatusLineTargetCatalog`

**Data, stays (9 files):** `StatusLineDeploymentConflictException.cs`, `StatusLineDeploymentFile.cs`, `StatusLineDeploymentPlan.cs`, `StatusLineFileChangeKind.cs`, `StatusLineOwnedFile.cs`, `StatusLinePlannedFileChange.cs`, `StatusLineReceipt.cs`, `StatusLineTarget.cs`, `UtilitiesStatusLineFileState.cs`


### Doctor

**Logic: 6 files, 6 types.** `CodeGraphIndexRules`, `CopilotLargeOutputProbe`, `CopilotPromptProbe`, `DocsEnabled`, `DoctorReportLines`, `PostCheckoutHookText`

**Data, stays (9 files):** `CodeGraphOutcome.cs`, `CodeGraphRestore.cs`, `DoctorCheck.cs`, `DoctorMode.cs`, `DoctorOptions.cs`, `DoctorReport.cs`, `RepositoryToolSettings.cs`, `WorktreeIndexOutcome.cs`, `WorktreeIndexStatus.cs`


### Agents

**Logic: 8 files, 8 types.** `AgentHarnessFilter`, `AgentPromptScanner`, `AgentRoutingEvaluator`, `AgentSet`, `AgentSpecValidator`, `AgentSyncLinter`, `MarkdownAgentParser`, `TomlAgentParser`

**Data, stays (4 files):** `Model/AgentModel.cs`, `Model/HarnessCapabilityProfile.cs`, `Model/HarnessKind.cs`, `Parsing/AgentLoadResult.cs`

**Interface-only, the whole file moves to `Contracts` (1 file):** `Parsing/IAgentParser.cs`


### RunStart

**Logic: 6 files, 6 types.** `GitWorktreePorcelain`, `RunBaseStateFormat`, `RunStartEvidenceRules`, `RunStartNaming`, `RunStartPlanner`, `RunStartResumeReader`

**Data, stays (3 files):** `GitOutcome.cs`, `RunRecord.cs`, `RunStartPlan.cs`


### Update

**Logic: 6 files, 6 types.** `BinaryInstaller`, `ChecksumVerifier`, `KyberDashGuidance`, `PlatformRid`, `ReleaseOrigin`, `ReleaseVersion`

**Data, stays (3 files):** `GitHubReleaseEndpoints.cs`, `SelfUpdateException.cs`, `SelfUpdateHost.cs`


### CopilotPrompt

**Logic: 4 files, 4 types.** `CopilotOverrideSettings`, `CopilotPromptFile`, `CopilotPromptHome`, `VsCodeUserSettingsPaths`


### Security

**Logic: 2 files, 2 types.** `InstructionSurfacePatterns`, `InstructionSurfaceScanner`

**Data, stays (1 file):** `InstructionSurfaceRuleCodes.cs`


### CodeGraph

**Data, stays (2 files):** `CodeGraphEdge.cs`, `CodeGraphNode.cs`


### FileSystem

**Logic: 1 file, 1 type.** `DocsRootPath`


## Part 2: every caller of Domain logic outside Application

A caller is a file outside `Domain` and `Application` that names a logic type the scan found, in code:
comments and string literals are stripped first, so a name that appears only in prose is not a caller.
A file that **declares** a type of the same name is not a caller either; those are listed separately
below. Counts are files.

| Project | Files | Route |
|---|---|---|
| `Data` | 65 | per row in the table below: move, split, or none (part 3, step 0b) |
| `Contracts` | 36 | one of the three decisions below, per type |
| `Core` | 13 | per row below: move, split, none, or delete the static locator (tasks 2.2 and 2.3) |
| `Core.Docs` | 0 | no caller; its own four files are deleted under task 2.2 (below) |
| `Cli` | 76 | none: `Cli` references `Application` |
| `Mcp` | 4 | none: `Mcp` references `Application` |
| `Arbiter` | 2 | none: `Arbiter` references `Application` |
| `Hooks` | 1 | none: `Hooks` references `Application` |
| `tests/KyberWeave.Tests` | 318 | none: the test projects reference `Application` |
| `tests/KyberWeave.Integration.Tests` | 80 | none: they reference `Application` |
| `tests/KyberWeave.Testing` | 16 | none: they reference `Application` |
| `tests/KyberWeave.Domain.Tests` | 5 | the test moves to `KyberWeave.Application.Tests` with its class |
| `tests/KyberWeave.Application.Tests` | 3 | already an `Application` test |
| `tests/KyberWeave.RepoPolicy.Tests` | 2 | none |

### How a caller row was routed

The class belongs to the Domain type; the route belongs to the caller, so one row that names types of
two classes carries two routes.

- **(a) data with a method.** The type is a record, class or exception that holds values and adds
  methods over them. `Domain` keeps the values as data and the methods move to an `Application` service
  or factory. A caller that only reads the values needs no route; one that calls a moved method takes a
  `Contracts` interface for it, or splits so that the call happens in `Application`.
- **(b) pure static helper.** The type computes only from values passed in, so requirement 4.3 moves it
  to `Application` as a static helper. `Data` and `Core` cannot call a static helper in `Application`,
  so the caller is itself logic and takes one of two routes. **move**: the whole file moves to
  `Application`, which is allowed because `Application` references `Data`. **split**: the file keeps a
  raw-I/O adapter in place and the part that computes moves to `Application`. A split is the right
  route when the file already talks to a resource: requirement 3.1 leaves the adapter no decisions, so
  the adapter returns the raw value and the logic lives one layer up.
  The two halves meet through a `Contracts` interface, never by naming each other's class: the adapter
  implements the interface its callers already take — all but five of the 35 split rows do — and the
  `Application` service takes that interface by injection, because `Data` cannot reference `Application`
  and `Application` may not name a concrete `Data` class (requirement 1.1). The five that gain one in
  step 0b are `DocGraphExporter`, `CopilotMcpServerProbe`, `SquadOperatorChannelOverlayFile`,
  `CopilotPromptRefresher` and `GitHubReleaseClient`.
  A `move` row whose file calls a resource directly is not moved as it stands: its seam conversion
  lands first, in its own commit (rule 2), so `move` means "move once the class calls no resource
  directly". `PlanRootResolver` is the case in point: it calls `Directory.Exists`, `File.Exists`,
  `File.ReadAllText`, `File.GetLastWriteTimeUtc` and `Path.GetFullPath` today.
- **(c) stateful service.** The type holds state or reads a resource, so it becomes an instance service
  behind a `Contracts` interface and the caller takes that interface.

`Core` has one extra constraint: requirement 4.1 stops it referencing `Application`, so no `Core` row
can go on calling an `Application` helper. Each `Core` row therefore moves (nothing else in `Core`
calls it, and `Cli` and `Mcp` reach it through `Application`), takes a `Contracts` interface, or is
deleted by task 2.2 or 2.3.

Only 28 types are not (b); the other 53 move as static helpers and need no per-type decision.

| type | class | what the split or the seam does |
|---|---|---|
| `ArbiterFactSet` | a | dictionary stays; `Contains`, `IsPresent`, `TryGet` and the lookups move |
| `ArbiterModelTargets` | a | record stays; `IsCompleteFor` moves |
| `ArbiterRule` | a | record stays; `ShouldAsk` moves |
| `ArbiterRunScope` | a | record stays; `Includes` and `Normalize` move |
| `PlanDocument` | a | record stays; `RedEvidenceRequirement` moves |
| `ConfigRegConfig` | a | the declared map stays; `Resolve` and `ModeWord` move |
| `DocumentModel` | a | property bag stays; the two `Append*` helpers move |
| `DocumentSet` | a | the model list stays; `ByComponent` moves |
| `EmbeddingUsage` | a | record stays; `Add` is internal, so no caller outside `Domain` sees the split |
| `HarnessProfileConfig` | a | record stays; `CloneWithProfiles` and `CreateProductDefaultProfiles` move |
| `KyberWeaveConfigLoadResult` | a | record stays (the `Contracts` decision is *narrow*); `Ok` and `Fail` move to an `Application` factory |
| `OntologyConfig` | a | record stays; `IsRequired*` and `With*` (except the copy helper) move |
| `OntologyConfigLoadResult` | a | record stays; `Ok` and `Fail` move |
| `ReleaseOrigin` | a | record stays; `Resolve`, `EnsureAllowed` and `IsLoopbackAuthority` move |
| `SeverityOverrides` | a | rows stay; `Parse`, `Find`, `Apply` and `RowsFor` move |
| `SquadModelPreset` | a | record stays; `ApplyTo` moves |
| `SquadPhysicalRootIdentity` | a | record stays; `Resolve`, `KeyOf` and `CanonicalizeAllowingMissing` move |
| `SquadRenderRequest` | a | record stays; the `Require*` guards and `Unprepared` move |
| `SquadSourceValidationException` | a | the exception stays with its constructors; nothing moves |
| `SquadDeploymentPlan` | a | its values and mutators are one object, so the whole type and its two `Contracts` interfaces move (the *move the interface* decision) |
| `ArbiterConfigLoader` | c | the embedded-rule read makes it a service behind a `Contracts` interface; its merge moves as a static helper |
| `DocGraphProjection` | c | a computed object built by a pure `Build`; `CandidateContracts.cs` moves with it |
| `GateCatalogueRenderer` | c | the embedded catalogue and the repository probe make it a service behind `IReviewGateCatalogue` |
| `MemoizedSquadGlobalRoots` | c | a cache over `ISquadGlobalRootResolver`, and it moves with that interface |
| `ProjectRootDetection` | c | it walks directories, so it becomes a service on the file-system seam |
| `SectionSelector` | c | already a service; it moves behind `IArbiterSectionSelectionService` |
| `SquadGlobalRoots` | c | reads the environment and files, so it becomes the `ISquadGlobalRootResolver` implementation |
| `SquadResponseGuidanceSource` | c | an embedded-resource accessor with a lazy cache, so it becomes a service |

### The one same-name definition the scan found


| file | name it declares | what to do |
|---|---|---|
| `src/KyberWeave.Data/FileSystem/DocsRootPath.cs` | `DocsRootPath` | declares its own `DocsRootPath`, as `src/KyberWeave.Domain/FileSystem/DocsRootPath.cs` does; it is not a caller. Reconcile the two definitions under the folder task that moves `DocsRootPath` (2.5.6), and do not add an injection point here. |

### src/KyberWeave.Data (65 files)

| file | Domain logic it names | class | route |
|---|---|---|---|
| `Arbiter/ConfiguredArbiterProviderFactory.cs` | `ArbiterKeyResolver` | b | move: a factory, so it moves with the provider it builds |
| `Arbiter/Facts/FileTextFacts.cs` | `DocsRootPath`, `SectionSelector` | b, c | split: the file read stays, excerpting moves; takes `ISectionSelector` |
| `Arbiter/Facts/GateReportFacts.cs` | `ArbiterFactSet`, `ReturnMarkerParser`, `ReviewJson`, `TextExcerpts` | a, b | move: `ArbiterFactSet` stays in `Domain` as data |
| `Arbiter/Facts/GitFacts.cs` | `GitDiffParser` | b | split: the git call stays, diff parsing moves |
| `Arbiter/FileArbiterModelTargetsReader.cs` | `ArbiterModelTargets` | a | none + a `Contracts` interface for `IsCompleteFor` |
| `Arbiter/FileArbiterUserOverrideWriter.cs` | `ArbiterUserSettings` | b | split: the write stays, the path and parse move |
| `Arbiter/FileArbiterUserSettings.cs` | `ArbiterConfigLoader`, `ArbiterUserSettings`, `RuleValidator` | b, c | split: the file read stays, merge and validation move; takes `IArbiterConfigResolver` |
| `Arbiter/Ledger/DecisionLog.cs` | `ArbiterDecisionQueries`, `ArbiterJson` | b | split: log append and read stay, the queries move |
| `Arbiter/Ledger/InFlightLedger.cs` | `ArbiterJson`, `ArbiterLedgerQueries`, `ArbiterRunScope` | a, b | split: the log read stays, the queries move; `ArbiterRunScope` stays data |
| `Arbiter/Plans/PlanFacts.cs` | `ArbiterFactSet`, `ArbiterTriggerCatalog`, `ModelTargetingRules`, `PlanDocument` … | a, b | move: both records stay in `Domain` |
| `Arbiter/Plans/PlanRootResolver.cs` | `HeaderBlock` | b | move after its seam conversion: `Directory.Exists`, `File.Exists`, `File.ReadAllText`, `File.GetLastWriteTimeUtc` and `Path.GetFullPath` return through `IFileSystem`, `ITimeProvider` and `IPathResolver` first |
| `Arbiter/Providers/SystemOneClient.cs` | `ArbiterFactSet`, `ArbiterRule`, `LensInstructions`, `RuleEngine` … | a, b | split: the HTTP call stays, rule evaluation moves |
| `Arbiter/Stores/ArbiterRunStore.cs` | `ArbiterJson`, `RefutationOutcome` | b | split: the ledger file stays, serialization and verdicts move |
| `Configuration/FileKyberWeaveConfigRepository.cs` | `KyberWeaveConfigAssembler`, `KyberWeaveConfigLoadResult`, `KyberWeaveYamlParser`, `OntologyConfig` … | a, b | split: the file read stays, assembly and the `Ok`/`Fail` factories move; lands with task 2.3 |
| `Configuration/FileUserHostConfigStore.cs` | `HostConfigYaml`, `KyberWeaveYamlParser`, `UserHostConfigPath` | b | split: the file read stays, yaml assembly moves |
| `Docs/Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs` | `EmbeddingUsage` | a | none: it builds the record, and `Add` is internal to `Domain` |
| `Docs/Export/DocGraphExporter.cs` | `DocGraphProjection`, `DocumentSet` | a, c | split: the write stays, the projection moves with `CandidateContracts.cs` |
| `Docs/FileSystem/PhysicalRepositoryFileStore.cs` | `DocsRootPath` | b | split: the store keeps the raw I/O, path normalisation moves; 2.5.6 reconciles the two `DocsRootPath` definitions |
| `Docs/GraphClaimCandidateSource.cs` | `LexicalSimilarity` | b | move |
| `Docs/Mcp/GitCheckoutProbe.cs` | `CorpusProvenance` | b | split: the git call stays, provenance formatting moves |
| `Docs/Mcp/GitWorktreeIdentity.cs` | `CorpusProvenance` | b | split: the git call stays, provenance formatting moves |
| `Docs/Parsing/DocumentLoader.cs` | `DocsRootPath`, `DocumentMarkdown`, `DocumentModel`, `DocumentSet` … | a, b | split: the file read stays, markdown parsing moves; `DocumentModel` and `DocumentSet` stay data |
| `Docs/Parsing/DocumentLoaderFactory.cs` | `OntologyConfig` | a | none |
| `Docs/SparseLexicalCandidateSource.cs` | `LexicalSimilarity` | b | move |
| `Doctor/ConfigFileToolSettings.cs` | `KyberWeaveConfigLoadResult` | a | none |
| `Doctor/FileCodeGraphIndexWorkspace.cs` | `CodeGraphIndexRules` | b | split: the process and file calls stay, the index rules move |
| `Hooks/ConventionTestClassResolver.cs` | `PathContainment` | b | move: already logic over an `IFileSystem` |
| `Hooks/YamlHooksConfigSource.cs` | `HooksConfigMerger`, `HooksConfigPath` | b | split: the file and environment reads stay, merging moves |
| `Review/FileCitedSourceFiles.cs` | `DocsRootPath` | b | split: the file read stays, path normalisation moves |
| `Review/FileContainedFileReader.cs` | `DocsRootPath` | b | split: the file read stays, containment moves |
| `Review/FileCoverageReportSource.cs` | `CoberturaReport` | b | split: the file read stays, the coverage parse moves |
| `Review/FileNotSeatedStore.cs` | `NotSeatedRecord` | b | split: the read and write stay, the record parse moves |
| `Review/FileRepositoryFileLister.cs` | `ProjectRootDetection` | c | split: the listing stays, root detection moves behind a `Contracts` interface |
| `Review/FileRepositoryTreeProbe.cs` | `TaskGatePlanning` | b | split: the file probe stays, the gate plan moves |
| `Review/FileReviewCycleStore.cs` | `ReviewCyclePlanning` | b | split: the folder read stays, cycle planning moves |
| `Review/FileSeverityOverrideSource.cs` | `ConfigRegConfig`, `SeverityOverrides` | a | split: it returns the text, and `Parse`, `Find` and `Apply` move with the type's methods |
| `Review/FileTestClassSources.cs` | `TestClassPlanning` | b | split: the file and process reads stay, class resolution moves |
| `Review/GitChangeReader.cs` | `GitChangeParsing` | b | split: the git call stays, name-status parsing moves |
| `RunStart/FileRunStateStore.cs` | `RunBaseStateFormat` | b | split: the file read stays, the record format moves |
| `RunStart/RunCheckoutGuard.cs` | `RunBaseStateFormat` | b | move: already logic over `IRunStateStore` and `IWritableFileSystem` |
| `Squad/Deployment/SquadDeploymentAdapters.cs` | `SquadDeploymentPlan`, `SquadModelPreset`, `SquadSourceValidationException` | a, b | move: already routed |
| `Squad/Deployment/SquadDeploymentPlanner.cs` | `SquadDeploymentPlan`, `SquadHookJsonBlock`, `SquadPathPolicy`, `SquadPhysicalRootIdentity` … | a, b | move: already routed |
| `Squad/Deployment/SquadHookJsonFile.cs` | `SquadHookJsonBlock` | b | move: already routed |
| `Squad/Deployment/SquadResponseGuidanceDeployer.cs` | `SquadPathPolicy`, `SquadResponseGuidanceSource` | b, c | move: already routed |
| `Squad/Deployment/SquadResponseGuidanceReader.cs` | `SquadResponseGuidanceSource` | c | move: already routed |
| `Squad/Deployment/SquadSkillShadowing.cs` | `SquadSkillOutputPaths` | b | move: already routed |
| `Squad/Deployment/SquadStateMigration.cs` | `SquadPhysicalRootIdentity` | a | move: already routed |
| `Squad/Deployment/SquadStateStore.cs` | `SquadDeploymentPlan`, `SquadPathPolicy`, `SquadPhysicalRootIdentity`, `SquadTargetCatalog` | a, b | move: already routed |
| `Squad/Deployment/SquadTargetResolver.cs` | `SquadTargetCatalog` | b | move: already routed |
| `Squad/Deployment/SquadTransaction.cs` | `SquadDeploymentPlan`, `SquadPathPolicy`, `SquadPhysicalRootIdentity` | a | move: already routed |
| `Squad/Deployment/SquadWorkerBaselineFile.cs` | `SquadWorkerBaselineInjector` | b | move: already routed |
| `Squad/Instances/SquadInstanceLinkSync.cs` | `SquadPhysicalRootIdentity` | a | move: the identity record stays, the sync is logic |
| `Squad/Packaging/SquadCompanionSkills.cs` | `SquadSourceValidationException` | a | none: it throws an exception that stays in `Domain` |
| `Squad/Packaging/SquadPacker.cs` | `SquadResponseGuidanceSource`, `SquadResponsePreferencesInjector` | b, c | move: a static class that becomes `ISquadPackager` |
| `Squad/Parsing/SquadSourceLoader.cs` | `CopilotToolCatalog`, `SquadModelPreset`, `SquadPathPolicy`, `SquadSourceValidator` | a, b | move: becomes `ISquadSourceLoader`, and `SquadModelPreset` stays data |
| `Squad/Presets/SquadModelPresetSelectionStore.cs` | `SquadPhysicalRootIdentity`, `SquadTargetCatalog` | a, b | split: the file stays, root identity and target lookups move |
| `Squad/Presets/SquadModelPresetStore.cs` | `SquadModelPreset` | a | none: it reads and writes the preset's own file |
| `Squad/Presets/SquadModelPresetValidator.cs` | `SquadModelPreset`, `SquadSourceValidator` | a, b | move |
| `Squad/Release/GitHubSquadReleaseSource.cs` | `ReleaseOrigin`, `ReleaseVersion` | a, b | split: HTTP stays, origin resolution and version parsing move |
| `Squad/Rendering/CopilotMcpServerProbe.cs` | `SquadOperatorChannel` | b | split: the config read stays, the channel rules move |
| `Squad/Rendering/FileSquadRenderInputReader.cs` | `MemoizedSquadGlobalRoots`, `SquadOperatorChannel`, `SquadRenderRequest` | a, b, c | split: the file, directory and environment reads stay, the request guards move, global roots move behind `ISquadGlobalRootResolver`; `SquadSourceLoader`, `SquadWorkerBaselineFile`, `CopilotMcpServerProbe` and `SquadOperatorChannelOverlayFile` are reached through interfaces, not as concrete classes |
| `Squad/Rendering/SquadOperatorChannelOverlayFile.cs` | `SquadOperatorChannel` | b | split: the file read stays, the overlay rules move |
| `Squad/SquadServiceCollectionExtensions.cs` | `SquadGlobalRoots` | c | none: it registers `ISquadGlobalRootResolver`, and the implementation moves |
| `Update/CopilotPromptRefresher.cs` | `CopilotPromptFile` | b | split: the write stays, the embedded text and validation move |
| `Update/GitHubReleaseClient.cs` | `ReleaseOrigin`, `ReleaseVersion` | a, b | split: HTTP stays, the origin resolve and guard move |
### src/KyberWeave.Core (13 files)

| file | Domain logic it names | class | route |
|---|---|---|---|
| `Configuration/KyberWeaveConfigLoader.cs` | `KyberWeaveConfigAssembler`, `KyberWeaveConfigLoadResult` | a, b | deleted by task 2.3; callers take `IKyberWeaveConfigRepository` |
| `Configuration/KyberWeaveYamlFile.cs` | `HarnessProfileConfig`, `HarnessProfileConfigLoader`, `OntologyConfig`, `OntologyConfigLoadResult` … | a, b | deleted by task 2.3 |
| `Configuration/ReviewConfigLoader.cs` | `ReviewConfigParser` | b | deleted by task 2.3 |
| `Doctor/CodeGraphProvisioner.cs` | `CodeGraphIndexRules` | b | move: becomes the `ICodeGraphProvisioner` implementation |
| `Doctor/DoctorFileSystem.cs` | `CopilotOverrideSettings` | b | split: the settings read stays, the parse moves |
| `Review/Catalogue/GateCatalogue.cs` | `GateCatalogueRenderer` | c | move: behind `IReviewGateCatalogue` |
| `Review/ChangedFiles.cs` | `GitChangeParsing` | b | move |
| `Review/ReviewDecisionModel.cs` | `ArbiterRule`, `ReviewDecisionText` | a, b | move: behind `IReviewDecisionModel` and `IReviewDecisionModelFactory` |
| `Review/ReviewLensApplicability.cs` | `ReviewLensRules` | b | move |
| `Review/ReviewVerdictDecisions.cs` | `ReviewVerdictRules`, `SeverityOverrides` | a, b | move: behind `IReviewDecisionSteps` |
| `Review/TaskGateSelection.cs` | `TaskGatePlanning` | b | move: behind `ITaskGateSelectionService` |
| `RunStart/RunBaseState.cs` | `RunBaseStateFormat` | b | deleted by task 2.2; callers take `IRunStateStore` |
| `RunStart/RunStartEvidence.cs` | `RunStartEvidenceRules` | b | none + `IRunStartEvidence`: the static becomes the interface |
### src/KyberWeave.Cli (76 files)

| file | Domain logic it names | route |
|---|---|---|
| `CliAssemblyVersion.cs` | `ReleaseVersion` | none: stays |
| `Commands/Agents/AgentScanCommand.cs` | `FailOnPolicy` | none: stays |
| `Commands/Arbiter/ArbiterAuditCommand.cs` | `RuleValidator` | none: stays |
| `Commands/Arbiter/ArbiterCommentCommand.cs` | `ArbiterRule`, `NoneProvider` | none: stays |
| `Commands/Arbiter/ArbiterEvalCommand.cs` | `ArbiterTriggerFamilies`, `RuleValidator` | none: stays |
| `Commands/Arbiter/ArbiterInstallCommand.cs` | `ArbiterKeyResolver`, `RuleValidator`, `SquadTargetCatalog` | none: stays |
| `Commands/Arbiter/ArbiterPlanCommand.cs` | `PlanDocument` | none: stays |
| `Commands/Arbiter/ArbiterRulesCommand.cs` | `ArbiterRule`, `ArbiterTriggerFamilies`, `RulePredicate`, `RuleValidator` | none: stays |
| `Commands/Arbiter/ArbiterSectionsCommand.cs` | `NoneProvider`, `SectionSelector` | none: stays |
| `Commands/Arbiter/ArbiterSetupCommand.cs` | `ArbiterCliText`, `ArbiterKeyResolver`, `ArbiterUserSettings`, `RuleValidator` | none: stays |
| `Commands/Arbiter/ArbiterStatusCommand.cs` | `ArbiterHookWiring`, `ArbiterKeyResolver`, `SquadTargetCatalog` | none: stays |
| `Commands/Arbiter/ArbiterTuneCommand.cs` | `RuleValidator` | none: stays |
| `Commands/Arbiter/ArbiterUninstallCommand.cs` | `ArbiterKeyResolver`, `KyberWeaveConfigLoadResult`, `RuleValidator`, `SquadTargetCatalog` | none: stays |
| `Commands/Arbiter/ArbiterValidateCommand.cs` | `RuleValidator` | none: stays |
| `Commands/CommandHelpers.cs` | `KyberWeaveConfigLoadResult` | none: stays |
| `Commands/Docs/DocsCatalogCommand.cs` | `DocumentModel`, `DocumentSet` | none: stays |
| `Commands/Docs/DocsCloseoutCommand.cs` | `OntologyConfig` | none: stays |
| `Commands/Docs/DocsCommandComposition.cs` | `ClaimExtractor`, `OntologyConfig` | none: stays |
| `Commands/Docs/DocsDriftCommand.cs` | `DocumentSet` | none: stays |
| `Commands/Docs/DocsFinalizeCommand.cs` | `OntologyConfig` | none: stays |
| `Commands/Docs/DocsGraphCommand.cs` | `DocumentSet` | none: stays |
| `Commands/Docs/DocsInitCommand.cs` | `HarnessMcpCatalog`, `HarnessMcpEntry`, `KyberWeaveConfigLoadResult`, `McpWiringReceipt` … | none: stays |
| `Commands/Docs/DocsMcpMigrateCommand.cs` | `HarnessMcpCatalog` | none: stays |
| `Commands/Docs/DocsMcpUnwireCommand.cs` | `McpWiringReceipt` | none: stays |
| `Commands/Docs/DocsMcpWireCommand.cs` | `HarnessMcpCatalog`, `HarnessMcpEntry` | none: stays |
| `Commands/Docs/DocsOnboardingCommand.cs` | `ConfigRegConfig`, `ConfigRegMarkdown`, `HostConfigYaml`, `KyberWeaveConfigLoadResult` | none: stays |
| `Commands/Docs/DocsPacketCommand.cs` | `PlanTaskPacket` | none: stays |
| `Commands/Docs/DocsStandardsDiffCommand.cs` | `OntologyConfig` | none: stays |
| `Commands/Docs/DocsStandardsMigrateCommand.cs` | `OntologyConfig` | none: stays |
| `Commands/Docs/DocsTasksCommand.cs` | `OntologyConfig`, `PacketSelfContainment`, `PlanTaskFileRules`, `PlanTaskPacket` | none: stays |
| `Commands/Docs/DocsValidateCommand.cs` | `DocumentSet`, `OntologyConfig` | none: stays |
| `Commands/Docs/RepositoryDocsAnalysisCommandService.cs` | `AnalysisGlossary`, `DocGraphProjection`, `DocumentSet` | none: stays |
| `Commands/Doctor/DoctorReportRenderer.cs` | `DoctorReportLines` | none: stays |
| `Commands/PlanArguments.cs` | `KyberWeaveConfigLoadResult` | none: stays |
| `Commands/Review/ReviewCatalogueCommand.cs` | `ReviewCommandResult` | none: stays |
| `Commands/Review/ReviewCycleCommand.cs` | `ReviewCommandResult` | none: stays |
| `Commands/Review/ReviewDuplicatesCommand.cs` | `ReviewCommandResult` | none: stays |
| `Commands/Review/ReviewGatesCommand.cs` | `ReviewCommandResult`, `ReviewGateReporting`, `TaskSlug` | none: stays |
| `Commands/Review/ReviewMigrateTaskGatesCommand.cs` | `KyberWeaveConfigAssembler`, `ReviewTaskGateMigration` | none: stays |
| `Commands/Review/ReviewSeatsCommand.cs` | `GitChangeParsing`, `ReviewCommandResult` | none: stays |
| `Commands/Review/ReviewSettings.cs` | `TaskSlug` | none: stays |
| `Commands/Review/ReviewSuppressionsCommand.cs` | `ReviewCommandResult` | none: stays |
| `Commands/Review/ReviewVerdictCommand.cs` | `ReviewVerdictLabel` | none: stays |
| `Commands/Run/RunDeliverCommand.cs` | `OntologyConfig` | none: stays |
| `Commands/Run/RunStartCommand.cs` | `BriefFormatter`, `RunStartPlanner` | none: stays |
| `Commands/Skills/CatalogCommand.cs` | `SkillCatalogRow` | none: stays |
| `Commands/Skills/LintCommand.cs` | `DescriptionScorer` | none: stays |
| `Commands/Skills/RouteCommand.cs` | `LexicalRoutingStrategy` | none: stays |
| `Commands/Skills/ScanCommand.cs` | `FailOnPolicy` | none: stays |
| `Commands/Squad/Brief/SquadBriefCommand.cs` | `BriefFormatter` | none: stays |
| `Commands/Squad/CopilotPromptCommands.cs` | `CopilotOverrideSettings`, `CopilotPromptFile`, `CopilotPromptHome`, `JsonServerSplice` … | none: stays |
| `Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs` | `KyberDashGuidance`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/Infrastructure/SquadPresetFileRewriter.cs` | `ArbiterModelTargetsFile`, `SquadModelResolution` | none: stays |
| `Commands/Squad/Infrastructure/SquadTargetRootConfirmation.cs` | `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadCommandComposition.cs` | `ArbiterHookTimeout`, `KyberWeaveConfigLoadResult`, `ReleaseVersion`, `SquadDeploymentPlan` … | none: stays |
| `Commands/Squad/SquadDoctorCommand.cs` | `KyberWeaveConfigLoadResult`, `SquadDeploymentPlan`, `SquadHookJsonBlock`, `SquadModelPreset` … | none: stays |
| `Commands/Squad/SquadHostDefaults.cs` | `ReleaseOrigin`, `SquadGlobalRoots` | none: stays |
| `Commands/Squad/SquadInstallCommand.cs` | `ArbiterHookWiring`, `KyberWeaveConfigLoadResult`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadInstanceRemoveCommand.cs` | `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadPresetClearCommand.cs` | `ArbiterModelTargetsFile`, `SquadDeploymentPlan`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadPresetListCommand.cs` | `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadPresetShowCommand.cs` | `SquadModelPreset`, `SquadModelResolution`, `SquadSourceValidationException`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadPresetUseCommand.cs` | `SquadDeploymentPlan`, `SquadModelPreset`, `SquadSourceValidationException`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadRendererSet.cs` | `AntigravityRenderer`, `ClaudeRenderer`, `CodexRenderer`, `CommandCodeRenderer` … | none: stays |
| `Commands/Squad/SquadRunCommand.cs` | `SquadInstanceCatalog`, `SquadModelPreset`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadStatusCommand.cs` | `SquadDeploymentPlan`, `SquadHookJsonBlock`, `SquadModelPreset`, `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadTaskGateHint.cs` | `KyberWeaveConfigLoadResult` | none: stays |
| `Commands/Squad/SquadUninstallCommand.cs` | `SquadTargetCatalog` | none: stays |
| `Commands/Squad/SquadUpdateCommand.cs` | `KyberWeaveConfigLoadResult`, `SquadTargetCatalog` | none: stays |
| `Commands/Utilities/UtilitiesStatusLineCommandComposition.cs` | `SquadPathPolicy`, `StatusLineTargetRoots` | none: stays |
| `Commands/Utilities/UtilitiesStatusLineDeployCommand.cs` | `StatusLineTargetRoots`, `UtilitiesStatusLineArtifacts`, `UtilitiesStatusLineTargetCatalog` | none: stays |
| `Commands/Utilities/UtilitiesStatusLineDoctorCommand.cs` | `SquadPathPolicy`, `StatusLineTargetRoots`, `UtilitiesStatusLineArtifacts`, `UtilitiesStatusLineHarnessProbe` … | none: stays |
| `Commands/Utilities/UtilitiesStatusLineOutput.cs` | `SquadPathPolicy`, `UtilitiesStatusLineActivationGuidance`, `UtilitiesStatusLineArtifacts`, `UtilitiesStatusLineTargetCatalog` | none: stays |
| `Commands/Utilities/UtilitiesStatusLineRemoveCommand.cs` | `StatusLineTargetRoots`, `UtilitiesStatusLineTargetCatalog` | none: stays |
| `Commands/Utilities/UtilitiesStatusLineStatusCommand.cs` | `StatusLineTargetRoots`, `UtilitiesStatusLineTargetCatalog` | none: stays |
| `Composition/SelfUpdateHostFactory.cs` | `PlatformRid` | none: stays |

### src/KyberWeave.Mcp (4 files)

| file | Domain logic it names | route |
|---|---|---|
| `CodeGraphFreshness.cs` | `CodeGraphIndexRules`, `KyberWeaveConfigLoadResult` | none: stays |
| `DocsRootRegistry.cs` | `KyberWeaveConfigLoadResult`, `OntologyConfig` | none: stays |
| `DocsToolsComposition.cs` | `OntologyConfig` | none: stays |
| `RepositoryRootResolver.cs` | `KyberWeaveConfigLoadResult` | none: stays |

### src/KyberWeave.Arbiter (2 files)

| file | Domain logic it names | route |
|---|---|---|
| `ArbiterContainer.cs` | `KyberWeaveConfigLoadResult` | none: stays |
| `Composition.cs` | `HarnessAdapterRegistry`, `HookFastPath` | none: stays |

### src/KyberWeave.Hooks (1 files)

| file | Domain logic it names | route |
|---|---|---|
| `HooksCli.cs` | `ClaudeHookEventParser`, `HookParseResult` | none: stays |

### `src/KyberWeave.Core.Docs` — no Domain caller, four files to delete

The project has these four files and none names a Domain logic type: `PlanPathResolver.cs`,
`LegacyPorts.cs`, `PacketTestCommand.cs`, `PlanTaskTarget.cs`. Two of them are exactly what task 2.2
must delete, and this inventory is the place that says so:

- `LegacyPorts.cs` holds `static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore()`
  — a `Data` adapter in a static field, which is also the proof that `Core.Docs` references `Data`
  today. Task 2.2 removes that reference and the class.
- `PlanTaskTarget.cs` and `PacketTestCommand.cs` each build
  `new PlanTaskTargetService(new PlanTaskService(LegacyPorts.Files), …)` in a static field. Task 2.2
  deletes those statics and their callers take the services by injection, which is what requirement
  1.5 asks for.

`PlanPathResolver.cs` has the same shape with `new PhysicalRepositoryFileStore()` and goes with them.
So `Core.Docs` reaches the moved Domain logic only after task 2.2 rewires it, not before.

### Tests (318 files in `KyberWeave.Tests`, 80 in `KyberWeave.Integration.Tests`, 16 in `KyberWeave.Testing`, 5 in `KyberWeave.Domain.Tests`, 3 in `KyberWeave.Application.Tests`)

Every test project references `Application` (directly or through the product project it tests), so a
test that names a moved class keeps compiling. Two groups still move: `KyberWeave.Domain.Tests` holds
five hook tests, which belong in `KyberWeave.Application.Tests` beside the class they test, and a test
of a moved class moves to that class's project under task 5.2. The list is not repeated here; it is
the same 398 files the scan found, and task 5.2 groups them by seam.

### Finding: `Contracts` names Domain logic, with a decision per type

`Contracts` references `Domain` for the records in its service interfaces, so a logic type that moves
to `Application` would make `Contracts` reference `Application`, which is a cycle. Each type below has
one decision, taken now so the move task does not have to guess:

- **split** — the record stays in `Domain` and the named helper methods move to an `Application`
  service. The `Contracts` signature does not change, because it already names the record.
- **move the interface** — the type is a service, not a record, and the `Contracts` interface exists
  only for it. The interface moves to `Application` with the implementation.
- **narrow** — the interface returns a `Domain` record instead of a type that carries factory methods;
  the factories move to an `Application` factory.


| logic type | files | Contracts files | decision | what moves |
|---|---|---|---|---|
| `ArbiterFactSet` | 3 | `IArbiterFactSources.cs`, `IGateReportFacts.cs`, `IPlanFacts.cs` | split | record stays; `Contains`, `IsPresent`, `TryGet` and the lookups move to an Application service |
| `ArbiterModelTargets` | 1 | `IArbiterModelTargetsReader.cs` | split | record stays; `IsCompleteFor` moves |
| `ArbiterRule` | 3 | `IArbiterEvaluator.cs`, `IReviewDecisionModel.cs`, `IReviewDecisionModelFactory.cs` | split | record stays; `ShouldAsk` moves |
| `ArbiterRunScope` | 2 | `IArbiterFactSources.cs`, `IArbiterLedger.cs` | split | record stays; `Includes` and `Normalize` move |
| `BriefFacts` | 1 | `ISquadBriefService.cs` | move the interface | a resolver over prompt text, not a record; `ISquadBriefService` moves to Application |
| `BriefMap` | 1 | `ISquadBriefService.cs` | move the interface | loads and indexes files; `ISquadBriefService` moves with it |
| `ConfigRegConfig` | 1 | `ISeverityOverrideSource.cs` | split | the declared map stays; `Resolve` and `ModeWord` move |
| `DocGraphProjection` | 1 | `CandidateContracts.cs` | move the interface | computes relationships; `CandidateContracts.cs` moves to Application |
| `DocumentModel` | 1 | `IDocumentSource.cs` | split | record stays; `AppendSectionKeywords` and `AppendSectionCodeRefs` move |
| `DocumentSet` | 4 | `IDocGraphContributor.cs`, `IDocsGateRunner.cs`, `IDocumentSource.cs`, `IDocsSchemaGate.cs` | split | record stays; `ByComponent` moves |
| `DuplicateReport` | 1 | `IDuplicateDetectionService.cs` | split | record stays; `Merge` and `ClusterId` move |
| `HarnessProfileConfig` | 1 | `IAgentGovernanceService.cs` | split | record stays; `CloneWithProfiles` and `CreateProductDefaultProfiles` move |
| `KyberWeaveConfigLoadResult` | 2 | `IKyberWeaveConfigRepository.cs`, `IArbiterConfigResolver.cs` | narrow | the record stays in Domain; `Ok` and `Fail` move to an Application factory, and the interface returns the record |
| `OntologyConfig` | 2 | `IDocumentSourceFactory.cs`, `IConfigLoadService.cs` | split | record stays; `IsRequired*` and `With*` (except the copy helper) move |
| `PlanDocument` | 1 | `IArbiterPlanService.cs` | split | record stays; `RedEvidenceRequirement` moves |
| `ReviewCommandResult` | 6 | `IReviewCatalogueService.cs`, `IReviewCycleService.cs`, `IReviewDuplicatesService.cs`, `IReviewGatesService.cs`, `IReviewSeatsService.cs`, `IReviewSuppressionsService.cs` | narrow | the record stays in Domain; `Refused` and `Reported` move to an Application factory |
| `ReviewPolicy` | 1 | `IReviewCycleSelector.cs` | split | record stays; `FixRoundTakes` moves |
| `SeverityOverrides` | 2 | `ISeverityOverrideSource.cs`, `IReviewDecisionSteps.cs` | split | record stays; `Find`, `Apply` and `RowsFor` move |
| `SquadDeploymentPlan` | 2 | `ISquadDeploymentPlanner.cs`, `ISquadTransaction.cs` | move the interface | `ISquadDeploymentPlanner` and `ISquadTransaction` move to Application with the plan |
| `SquadModelPreset` | 2 | `ISquadModelPresetStore.cs`, `ISquadModelPresetValidator.cs` | split | record stays; `ApplyTo` moves |
| `SquadRenderRequest` | 1 | `ISquadRenderInputReader.cs` | split | record stays; the `Require*` guards and `Unprepared` move |
| `StatusLineTargetRoots` | 1 | `IStatusLineDeploymentService.cs` | split | record stays; the `Resolve*` helpers move |

Two of them (`ReviewCommandResult`, `KyberWeaveConfigLoadResult`) are the only ones where neither of
the first two fits cleanly: their factory methods are the logic and they belong to six and two
`Contracts` files respectively, so the record stays and the factories go to an `Application` factory.
`SquadDeploymentPlan` is the only type whose whole interface moves, because `ISquadDeploymentPlanner`
and `ISquadTransaction` exist for the deployment logic that phase 3 moves anyway.

## Part 3: the move, in order

Three stages. The first two are what make the third mechanical.

**Step 0a — resolve the 36 `Contracts` findings.** Apply the decision in part 2's finding table for
each of the 22 types (`split`, `move the interface`, `narrow`): the record, the `Contracts` interface
and the `Application` factory, helper or service are put in place, and `Contracts` stops naming
anything that will move. This goes first because `Contracts` references `Domain` and may never
reference `Application`; once a logic type has moved, a `Contracts` file that still names it can be
made to compile only by giving that project a reference to `Application`, which is the cycle the
layers forbid.

**Step 0b — split the (a) types and reroute the callers.** Apply the 20 (a) splits and the 8 (c)
seams, then the 65 `Data` and 13 `Core` routes in part 2: each `move` row moves, each `split` row
leaves its raw-I/O adapter behind and hands its logic to `Application`, and each `none` row is
rechecked against the new shape. This is what makes a folder move compile: `Data` and `Contracts` do
not reference `Application`, so an unrouted caller of a moved type breaks the moment the type lands.

**Step 1 — the folder moves.** Then each task below is a `git mv` of a folder's logic files and their
tests, plus the path-keyed artefacts in the rules below, and the namespace does not change. The order
inside step 1 barely matters: after steps 0a and 0b, every remaining reference is either inside
`Application` or through a `Contracts` interface, so a `depends on` cycle between groups is harmless.
The groups are still listed most-named first, because that is the order that keeps each move's
call-site diff smallest.

The first version of this part claimed the moves need no particular order. That holds only for the
callers that reference `Application` — `Cli`, `Mcp`, `Arbiter`, `Hooks` and every test project — which
compile against a moved class wherever it sits. It does not hold for `Data` and `Contracts`, which is
why they are steps 0a and 0b.

### Rules every move task follows

1. **A move commit is `git mv` only.** No file it moves changes content in that commit, and the build
   and the tests that covered the class are green before and after it. Anything else — a namespace, a
   `using`, a call site that has to change — is either the build fix the move forces, in its own
   commit, or a step 0b change that should already be in place.
2. **A seam conversion is a separate commit.** Turning a static class into an instance service,
   replacing a resource call with a seam, or taking a `Contracts` interface in a caller lands on its
   own, before or after the move, never inside it, so a review reads one commit as movement and the
   next as behaviour.
3. **Every move updates the path-keyed artefacts**, because a file's path is a key in four places:
   - document code references: any `docs/` document that names the file, then `kyber-weave docs drift .`
     to prove the corpus still matches the code;
   - `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`, when the moved class
     starts a process;
   - the `Domain` rows of `docs/reference/seams.md`, when the moved class is an adapter or gains a
     seam;
   - the `tests/KyberWeave.Domain.Tests` files of the moved classes: the test moves to
     `tests/KyberWeave.Application.Tests` beside its class, and task 5.2 groups it by seam afterwards.
4. **The order is steps 0a, 0b, then the folders**, with `Security` (2.5.10) first as the pilot.

### The 24 folder tasks

| task | Domain group | logic classes | files | depends on | callers outside Application |
|---|---|---|---|---|---|
| 2.5.1 | `Configuration` | 23 | 23 | task 2.3, then Arbiter: root, FileSystem, Review: catalogue and commands, Review: gates and cycles, Squad: deployment, briefs and the rest | KyberWeave.Application.Tests, KyberWeave.Arbiter, KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Mcp, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.2 | `Arbiter: Rules and providers` | 18 | 16 | Arbiter: Facts, Arbiter: root, FileSystem | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.3 | `Squad: deployment, briefs and the rest` | 24 | 23 | Configuration | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.RepoPolicy.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.4 | `Arbiter: root` | 16 | 12 | Arbiter: Facts, Arbiter: Rules and providers, Configuration, Squad: deployment, briefs and the rest | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.5 | `Arbiter: Facts` | 26 | 23 | Arbiter: Rules and providers, Arbiter: root | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.6 | `FileSystem` | 1 | 1 | none | KyberWeave.Data, KyberWeave.Tests |
| 2.5.7 | `Review: gates and cycles` | 15 | 14 | Arbiter: Rules and providers, Configuration, Review: duplicates and suppression, Review: seats and verdicts | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.8 | `Review: seats and verdicts` | 15 | 12 | Configuration, Review: duplicates and suppression, Review: gates and cycles | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.9 | `Review: duplicates and suppression` | 5 | 4 | Arbiter: Rules and providers | KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.10 | `Security` — **the pilot** | 2 | 2 | none | none |
| 2.5.11 | `Docs: validation and models` | 22 | 22 | Arbiter: Rules and providers, Arbiter: root, Configuration, FileSystem, Review: seats and verdicts | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.12 | `Squad: rendering support` | 18 | 16 | Arbiter: Facts, Arbiter: Rules and providers, Squad: deployment, briefs and the rest | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.13 | `Skills` | 11 | 11 | Agents, Security | KyberWeave.Cli, KyberWeave.Tests |
| 2.5.14 | `Agents` | 8 | 8 | Configuration, Security, Skills | KyberWeave.Tests |
| 2.5.15 | `Review: catalogue and commands` | 7 | 6 | Configuration, Review: gates and cycles, Review: seats and verdicts | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.RepoPolicy.Tests, KyberWeave.Tests |
| 2.5.16 | `CopilotPrompt` | 4 | 4 | none | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.17 | `Squad: renderers` | 27 | 15 | Configuration, Squad: deployment, briefs and the rest, Squad: rendering support | KyberWeave.Cli, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.18 | `Arbiter: Hooks` | 25 | 22 | Arbiter: Facts, Arbiter: Rules and providers, Arbiter: root, Configuration | KyberWeave.Arbiter, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.19 | `Docs: analysis, MCP and scaffolding` | 23 | 19 | Configuration, Docs: validation and models | KyberWeave.Cli, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.20 | `Hooks` | 7 | 7 | none | KyberWeave.Application.Tests, KyberWeave.Data, KyberWeave.Domain.Tests, KyberWeave.Hooks |
| 2.5.21 | `StatusLine` | 7 | 7 | none | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Tests |
| 2.5.22 | `Doctor` | 6 | 6 | CopilotPrompt, FileSystem | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Mcp, KyberWeave.Tests |
| 2.5.23 | `RunStart` | 6 | 6 | Arbiter: Rules and providers, Configuration, Squad: deployment, briefs and the rest | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.24 | `Update` | 6 | 6 | none | KyberWeave.Cli, KyberWeave.Data, KyberWeave.Testing, KyberWeave.Tests |

24 tasks, the same shape task 2.5 describes: the folder in `Domain` is left holding data only,
each moved class gains or keeps a unit test through fakes, and behaviour does not change
(requirement 10.1). Task 2.6 then confirms the end state.

`Security` (2.5.10) is the pilot: 2 logic classes in 2 files, no caller outside `Application` and no
dependency on another group, so it proves the move mechanics and the artefact list above at the
smallest possible size before a 26-class group runs. `Configuration` (2.5.1) waits for task 2.3: the
static loader retirement is what puts `KyberWeaveConfigAssembler`, `KyberWeaveYamlParser` and
`ReviewConfigParser` behind `IKyberWeaveConfigRepository`, so that folder cannot move before it.

## Limits

- The scan matches type names, not resolved symbols. Comments and string literals are stripped, and a
  same-name definition is excluded (the one found is listed in part 2), but a name declared in another
  project under a different meaning would still count. The file list for each project that needs a
  route was reviewed; the route is a property of the folder, not of the single file.
- The scan classifies by declaration shape. A record whose only method is a real algorithm under a
  name in the trivial list (`Equals`, `ToString`) would be counted as data; none of the 20 read by hand
  was.
- A file with one data type and one logic type counts as one logic file, and the logic type count is
  per type, so the two totals do not add up to the file count.
- A dependency edge is a type name used in the group's code with comments and string literals stripped,
  so it can still come from a name in a generic constraint or an attribute; the edges were not read
  line by line.
- The class is one per type, and a few types span two: `ArbiterConfigLoader` is (c) by its
  embedded-resource read and (b) by its merge, `SquadResponseGuidanceSource` is (c) by its embedded
  resource and (b) by the text it builds. The caller table names both where that happens. The route,
  not the class, is what a move task acts on.
- The route for each row was decided from the type's declaration and the caller's shape, not from a
  call-by-call reading of all 78 files. Where a row's route says "none", the check that remains is
  mechanical: the caller names only data members.

