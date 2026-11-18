---
id: specs/interface-driven-unit-tests/inventory/domain-move
title: Inventory of Domain's data and logic, and the callers of its logic
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of Domain's data and logic, and the callers of its logic

Produced 2026-11-17 for task 2.1 of the [interface-driven unit tests specification](../README.md),
on branch `opencode/idt/domain-plan` at `0409d910`, by a script run over every `.cs` file under `src/KyberWeave.Domain`,
followed by a read of 20 classifications. Evidence for the specification, not living guidance: it goes
stale as the move tasks land.

Owner decision D7 sets the end state: `KyberWeave.Domain` holds data only (records, DTOs, property
bags, value objects, enums, options, constants) and all logic moves to `KyberWeave.Application`.

## Bottom line

- **445 `.cs` files**: **155** stay (data), **285** hold logic and move,
  **5** hold nothing but interface declarations and move to `Contracts`.
  322 logic types are declared in those 285 files. 7 of the 155 data files also declare
  an interface: the records stay and only the interface is lifted to `Contracts`, so those files are
  never moved whole.
- **Namespaces do not change.** Domain declares its types under `KyberWeave.Core` (350 files),
  `KyberWeave.Domain` (48), `KyberWeave.Arbiter` (29), `KyberWeave.Cli` (17) and `KyberWeave.Mcp` (1),
  and `Application` already carries files in those namespaces (25 in `KyberWeave.Application.Review`,
  14 in `KyberWeave.Core.Docs.Validation`, 9 in `KyberWeave.Mcp`), so a moved class keeps its namespace.
  A project that already references `Application` needs no source change at its call sites.
- **Only four projects cannot reach the moved code**: `Data` (because `Application` references it),
  `Contracts` (a reference back would be a cycle), and `Core` and `Core.Docs` (task 2.2 removes their
  `Application` reference). 66 files in `Data`,
  14 in `Core` and 36 in `Contracts` name a Domain logic type.
  `Cli` (76 files), `Mcp` (4),
  `Arbiter` (2) and `Hooks` (1) already
  reference `Application` and are unaffected.
- **22 Domain logic types are named by 36 files in `Contracts`.** That is the
  finding the task asks for; part 2 lists it type by type.
- **24 move tasks**, one per Domain folder or group of subfolders, none over 30 logic classes.

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
- **interface-only (the whole file moves to `Contracts`)** — every type it declares is an
  `interface`. It declares no logic, but D7's list of what Domain keeps does not include interfaces,
  and `Contracts` already references `Domain` for the records those interfaces name.
- The scan is a C#-shaped parser, not Roslyn: it strips comments and string literals, finds every type
  declaration with its body, then classifies each top-level member by its declaration header. It was
  compared against the compiler's own view by counting declarations: 968 types across 445 files.

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
| Arbiter/Credentials/KeychainUnavailableException.cs | data | data | disagreed before the fix: read `method base` from a constructor’s `: base(...)`; a constructor is data |
| Arbiter/Facts/CommentSyntax.cs | logic | logic | disagreed before the fix: read a field initializer’s `new(...)` as a method |
| Squad/Rendering/SquadCollectionExtensions.cs | logic | logic | disagreed before the fix: an extension method was read as data |
| Docs/Validation/TodoInventoryValidator.cs | logic | logic | disagreed before the fix: `Validate(DocumentSet)` was read as own-value validation |
| Squad/Model/CopilotToolCatalog.cs | logic | logic | disagreed before the fix: `Normalize` was in the trivial list |
| Configuration/ConfigRegConfig.cs | logic | logic | disagreed before the fix: read `method readonly` from a tuple-typed field |
| Squad/Briefs/BriefFacts.cs | logic | logic | disagreed before the fix: read `method static` from a tuple return type |
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

A caller is a file outside `Domain` and `Application` that names a logic type the scan found, in code
(not in a `using` line or a comment). Counts are files. The route is what the caller does when that
type moves.

| Project | Files | Route |
|---|---|---|
| `Data` | 65 | take a `Contracts` interface, or the caller itself moves to `Application` (the deployment services do) |
| `Contracts` | 36 | narrow the signature to a Domain record, or move the interface to `Application` (finding, below) |
| `Core` | 14 | take a `Contracts` interface, or delete the static locator (task 2.2) |
| `Core.Docs` | 0 | no caller; its own four files are deleted under task 2.2 (below) |
| `Cli` | 76 | none: `Cli` references `Application` |
| `Mcp` | 4 | none: `Mcp` references `Application` |
| `Arbiter` | 2 | none: `Arbiter` references `Application` |
| `Hooks` | 1 | none: `Hooks` references `Application` |
| `tests/KyberWeave.Tests` | 326 | none: the test projects reference `Application` |
| `tests/KyberWeave.Integration.Tests` | 80 | none: they reference `Application` |
| `tests/KyberWeave.Testing` | 18 | none: they reference `Application` |
| `tests/KyberWeave.Domain.Tests` | 5 | the test moves to `KyberWeave.Application.Tests` with its class |
| `tests/KyberWeave.Application.Tests` | 3 | already an `Application` test |
| `tests/KyberWeave.RepoPolicy.Tests` | 2 | none |



### `src/KyberWeave.Core.Docs` — no Domain caller, four files to delete

The project has four `.cs` files and none names a Domain logic type: `PlanPathResolver.cs`,
`LegacyPorts.cs`, `PacketTestCommand.cs`, `PlanTaskTarget.cs`. Two of them are exactly what task 2.2
must delete, and this inventory is the place that says so:

- `LegacyPorts.cs` holds `static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore()`
  — a `Data` adapter in a static field, which is also the proof that `Core.Docs` references `Data`
  today. Task 2.2 removes that reference and the class.
- `PlanTaskTarget.cs` and `PacketTestCommand.cs` each build
  `new PlanTaskTargetService(new PlanTaskService(LegacyPorts.Files), …)` in a static field. Task 2.2
  deletes those statics, and their callers take the services by injection (requirement 1.5).
- `PlanPathResolver.cs` has the same shape with `new PhysicalRepositoryFileStore()` and goes with them.

So `Core.Docs` reaches the moved Domain logic only after task 2.2 rewires it, not before. The table row
above counts callers of Domain logic, which is why it is zero; the four files are still work, and task
2.2 owns them.

### The one same-name definition the scan found

A file that **declares** a type of the same name is not a caller of Domain's type, so it takes no
route here:

| file | name it declares | what to do |
|---|---|---|
| `src/KyberWeave.Data/FileSystem/DocsRootPath.cs` | `DocsRootPath` | It declares its own `internal static class DocsRootPath`, as `src/KyberWeave.Domain/FileSystem/DocsRootPath.cs` does. Reconcile the two definitions under the folder task that moves `DocsRootPath` (2.5.6); do not add an injection point to this file. |

### src/KyberWeave.Data (65 files)

| file | Domain logic it names | route |
|---|---|---|
| `Arbiter/ConfiguredArbiterProviderFactory.cs` | `ArbiterKeyResolver` | Contracts interface |
| `Arbiter/Facts/FileTextFacts.cs` | `DocsRootPath`, `SectionSelector` | Contracts interface |
| `Arbiter/Facts/GateReportFacts.cs` | `ArbiterFactSet`, `ReturnMarkerParser`, `ReviewJson`, `TextExcerpts` | Contracts interface |
| `Arbiter/Facts/GitFacts.cs` | `GitDiffParser` | Contracts interface |
| `Arbiter/FileArbiterModelTargetsReader.cs` | `ArbiterModelTargets` | Contracts interface |
| `Arbiter/FileArbiterUserOverrideWriter.cs` | `ArbiterUserSettings` | Contracts interface |
| `Arbiter/FileArbiterUserSettings.cs` | `ArbiterConfigLoader`, `ArbiterUserSettings`, `RuleValidator` | Contracts interface |
| `Arbiter/Ledger/DecisionLog.cs` | `ArbiterDecisionQueries`, `ArbiterJson` | Contracts interface |
| `Arbiter/Ledger/InFlightLedger.cs` | `ArbiterJson`, `ArbiterLedgerQueries`, `ArbiterRunScope` | Contracts interface |
| `Arbiter/Plans/PlanFacts.cs` | `ArbiterFactSet`, `ArbiterTriggerCatalog`, `ModelTargetingRules`, `PlanDocument` … | Contracts interface |
| `Arbiter/Plans/PlanRootResolver.cs` | `HeaderBlock` | Contracts interface |
| `Arbiter/Providers/SystemOneClient.cs` | `ArbiterFactSet`, `ArbiterRule`, `LensInstructions`, `RuleEngine` … | Contracts interface |
| `Arbiter/Stores/ArbiterRunStore.cs` | `ArbiterJson`, `RefutationOutcome` | Contracts interface |
| `Configuration/FileKyberWeaveConfigRepository.cs` | `KyberWeaveConfigAssembler`, `KyberWeaveConfigLoadResult`, `KyberWeaveYamlParser`, `OntologyConfig` … | Contracts interface |
| `Configuration/FileUserHostConfigStore.cs` | `HostConfigYaml`, `KyberWeaveYamlParser`, `UserHostConfigPath` | Contracts interface |
| `Docs/Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs` | `EmbeddingUsage` | Contracts interface |
| `Docs/Export/DocGraphExporter.cs` | `DocGraphProjection`, `DocumentSet` | Contracts interface |
| `Docs/FileSystem/PhysicalRepositoryFileStore.cs` | `DocsRootPath` | Contracts interface |
| `Docs/GraphClaimCandidateSource.cs` | `LexicalSimilarity` | Contracts interface |
| `Docs/Mcp/GitCheckoutProbe.cs` | `CorpusProvenance` | Contracts interface |
| `Docs/Mcp/GitWorktreeIdentity.cs` | `CorpusProvenance` | Contracts interface |
| `Docs/Parsing/DocumentLoader.cs` | `DocsRootPath`, `DocumentMarkdown`, `DocumentModel`, `DocumentSet` … | Contracts interface |
| `Docs/Parsing/DocumentLoaderFactory.cs` | `OntologyConfig` | Contracts interface |
| `Docs/SparseLexicalCandidateSource.cs` | `LexicalSimilarity` | Contracts interface |
| `Doctor/ConfigFileToolSettings.cs` | `KyberWeaveConfigLoadResult` | Contracts interface |
| `Doctor/FileCodeGraphIndexWorkspace.cs` | `CodeGraphIndexRules` | Contracts interface |
| `Hooks/ConventionTestClassResolver.cs` | `PathContainment` | Contracts interface |
| `Hooks/YamlHooksConfigSource.cs` | `HooksConfigMerger`, `HooksConfigPath` | Contracts interface |
| `Review/FileCitedSourceFiles.cs` | `DocsRootPath` | Contracts interface |
| `Review/FileContainedFileReader.cs` | `DocsRootPath` | Contracts interface |
| `Review/FileCoverageReportSource.cs` | `CoberturaReport` | Contracts interface |
| `Review/FileNotSeatedStore.cs` | `NotSeatedRecord` | Contracts interface |
| `Review/FileRepositoryFileLister.cs` | `ProjectRootDetection` | Contracts interface |
| `Review/FileRepositoryTreeProbe.cs` | `TaskGatePlanning` | Contracts interface |
| `Review/FileReviewCycleStore.cs` | `ReviewCyclePlanning` | Contracts interface |
| `Review/FileSeverityOverrideSource.cs` | `ConfigRegConfig`, `SeverityOverrides` | Contracts interface |
| `Review/FileTestClassSources.cs` | `TestClassPlanning` | Contracts interface |
| `Review/GitChangeReader.cs` | `GitChangeParsing` | Contracts interface |
| `RunStart/FileRunStateStore.cs` | `RunBaseStateFormat` | Contracts interface |
| `RunStart/RunCheckoutGuard.cs` | `RunBaseStateFormat` | Contracts interface |
| `Squad/Deployment/SquadDeploymentAdapters.cs` | `SquadDeploymentPlan`, `SquadModelPreset`, `SquadSourceValidationException` | move to Application |
| `Squad/Deployment/SquadDeploymentPlanner.cs` | `SquadDeploymentPlan`, `SquadHookJsonBlock`, `SquadPathPolicy`, `SquadPhysicalRootIdentity` … | move to Application |
| `Squad/Deployment/SquadHookJsonFile.cs` | `SquadHookJsonBlock` | move to Application |
| `Squad/Deployment/SquadResponseGuidanceDeployer.cs` | `SquadPathPolicy`, `SquadResponseGuidanceSource` | move to Application |
| `Squad/Deployment/SquadResponseGuidanceReader.cs` | `SquadResponseGuidanceSource` | move to Application |
| `Squad/Deployment/SquadSkillShadowing.cs` | `SquadSkillOutputPaths` | move to Application |
| `Squad/Deployment/SquadStateMigration.cs` | `SquadPhysicalRootIdentity` | move to Application |
| `Squad/Deployment/SquadStateStore.cs` | `SquadDeploymentPlan`, `SquadPathPolicy`, `SquadPhysicalRootIdentity`, `SquadTargetCatalog` | move to Application |
| `Squad/Deployment/SquadTargetResolver.cs` | `SquadTargetCatalog` | move to Application |
| `Squad/Deployment/SquadTransaction.cs` | `SquadDeploymentPlan`, `SquadPathPolicy`, `SquadPhysicalRootIdentity` | move to Application |
| `Squad/Deployment/SquadWorkerBaselineFile.cs` | `SquadWorkerBaselineInjector` | move to Application |
| `Squad/Instances/SquadInstanceLinkSync.cs` | `SquadPhysicalRootIdentity` | Contracts interface |
| `Squad/Packaging/SquadCompanionSkills.cs` | `SquadSourceValidationException` | Contracts interface |
| `Squad/Packaging/SquadPacker.cs` | `SquadResponseGuidanceSource`, `SquadResponsePreferencesInjector` | Contracts interface |
| `Squad/Parsing/SquadSourceLoader.cs` | `CopilotToolCatalog`, `SquadModelPreset`, `SquadPathPolicy`, `SquadSourceValidator` | Contracts interface |
| `Squad/Presets/SquadModelPresetSelectionStore.cs` | `SquadPhysicalRootIdentity`, `SquadTargetCatalog` | Contracts interface |
| `Squad/Presets/SquadModelPresetStore.cs` | `SquadModelPreset` | Contracts interface |
| `Squad/Presets/SquadModelPresetValidator.cs` | `SquadModelPreset`, `SquadSourceValidator` | Contracts interface |
| `Squad/Release/GitHubSquadReleaseSource.cs` | `ReleaseOrigin`, `ReleaseVersion` | Contracts interface |
| `Squad/Rendering/CopilotMcpServerProbe.cs` | `SquadOperatorChannel` | Contracts interface |
| `Squad/Rendering/FileSquadRenderInputReader.cs` | `MemoizedSquadGlobalRoots`, `SquadOperatorChannel`, `SquadRenderRequest` | Contracts interface |
| `Squad/Rendering/SquadOperatorChannelOverlayFile.cs` | `SquadOperatorChannel` | Contracts interface |
| `Squad/SquadServiceCollectionExtensions.cs` | `SquadGlobalRoots` | Contracts interface |
| `Update/CopilotPromptRefresher.cs` | `CopilotPromptFile` | Contracts interface |
| `Update/GitHubReleaseClient.cs` | `ReleaseOrigin`, `ReleaseVersion` | Contracts interface |

### src/KyberWeave.Core (14 files)

| file | Domain logic it names | route |
|---|---|---|
| `Configuration/KyberWeaveConfigLoader.cs` | `KyberWeaveConfigAssembler`, `KyberWeaveConfigLoadResult` | Contracts interface |
| `Configuration/KyberWeaveYamlFile.cs` | `HarnessProfileConfig`, `HarnessProfileConfigLoader`, `OntologyConfig`, `OntologyConfigLoadResult` … | Contracts interface |
| `Configuration/ReviewConfigLoader.cs` | `ReviewConfigParser` | Contracts interface |
| `Doctor/CodeGraphProvisioner.cs` | `CodeGraphIndexRules` | Contracts interface |
| `Doctor/DoctorFileSystem.cs` | `CopilotOverrideSettings` | Contracts interface |
| `Review/Catalogue/GateCatalogue.cs` | `GateCatalogueRenderer` | Contracts interface |
| `Review/ChangedFiles.cs` | `GitChangeParsing` | Contracts interface |
| `Review/ReviewCycles.cs` | `ReviewCyclePlanning` | Contracts interface |
| `Review/ReviewDecisionModel.cs` | `ArbiterRule`, `ReviewDecisionText` | Contracts interface |
| `Review/ReviewLensApplicability.cs` | `ReviewLensRules` | Contracts interface |
| `Review/ReviewVerdictDecisions.cs` | `ReviewVerdictRules`, `SeverityOverrides` | Contracts interface |
| `Review/TaskGateSelection.cs` | `TaskGatePlanning` | Contracts interface |
| `RunStart/RunBaseState.cs` | `RunBaseStateFormat` | Contracts interface |
| `RunStart/RunStartEvidence.cs` | `RunStartEvidenceRules` | Contracts interface |

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

### Tests (326 files in `KyberWeave.Tests`, 80 in `KyberWeave.Integration.Tests`, 18 in `KyberWeave.Testing`, 5 in `KyberWeave.Domain.Tests`, 3 in `KyberWeave.Application.Tests`)

Every test project references `Application` (directly or through the product project it tests), so a
test that names a moved class keeps compiling. Two groups still move: `KyberWeave.Domain.Tests` holds
five hook tests, which belong in `KyberWeave.Application.Tests` beside the class they test, and a test
of a moved class moves to that class's project under task 5.2. The list is not repeated here; it is the
same 406 files the scan found, and task 5.2 groups them by seam.


### Finding: `Contracts` names Domain logic, with a decision per type

`Contracts` references `Domain` for the records in its service interfaces, so a logic type that moves
to `Application` would make `Contracts` reference `Application`, which is a cycle. Each type below has
one decision, taken now so a move task does not have to guess:

- **split** — the record stays in `Domain` and the named helper methods move to an `Application`
  service. The `Contracts` signature does not change, because it already names the record.
- **move the interface** — the type is a service, not a record, and the interface exists only for it,
  so the interface moves to `Application` with the implementation and is registered in its composition
  root.
- **narrow** — the interface returns a `Domain` record instead of a type that carries factory methods;
  the factories move to an `Application` factory.

| logic type | files | Contracts files | decision | what moves |
|---|---|---|---|---|
| `ArbiterFactSet` | 3 | `IArbiterFactSources.cs`, `IGateReportFacts.cs`, `IPlanFacts.cs` | split | the record stays; `Contains`, `IsPresent`, `TryGet` and the `With` lookups move to an Application service |
| `ArbiterModelTargets` | 1 | `IArbiterModelTargetsReader.cs` | split | the record stays; `IsCompleteFor` moves |
| `ArbiterRule` | 3 | `IArbiterEvaluator.cs`, `IReviewDecisionModel.cs`, `IReviewDecisionModelFactory.cs` | split | the record stays; `ShouldAsk` moves |
| `ArbiterRunScope` | 2 | `IArbiterFactSources.cs`, `IArbiterLedger.cs` | split | the record stays; `Includes` and `Normalize` move |
| `BriefFacts` | 1 | `ISquadBriefService.cs` | move the interface | a resolver over prompt text, not a record; `ISquadBriefService` moves to Application |
| `BriefMap` | 1 | `ISquadBriefService.cs` | move the interface | loads and indexes files; `ISquadBriefService` moves with it |
| `ConfigRegConfig` | 1 | `ISeverityOverrideSource.cs` | split | the declared map stays; `Resolve` and `ModeWord` move |
| `DocGraphProjection` | 1 | `CandidateContracts.cs` | move the interface | computes document relationships; `CandidateContracts.cs` moves to Application |
| `DocumentModel` | 1 | `IDocumentSource.cs` | split | the record stays; `AppendSectionKeywords` and `AppendSectionCodeRefs` move |
| `DocumentSet` | 4 | `IDocGraphContributor.cs`, `IDocsGateRunner.cs`, `IDocumentSource.cs`, `IDocsSchemaGate.cs` | split | the record stays; `ByComponent` moves |
| `DuplicateReport` | 1 | `IDuplicateDetectionService.cs` | split | the record stays; `Merge` and `ClusterId` move |
| `HarnessProfileConfig` | 1 | `IAgentGovernanceService.cs` | split | the record stays; `CloneWithProfiles` and `CreateProductDefaultProfiles` move |
| `KyberWeaveConfigLoadResult` | 2 | `IKyberWeaveConfigRepository.cs`, `IArbiterConfigResolver.cs` | narrow | the record stays in Domain; `Ok` and `Fail` move to an Application factory and the interface returns the record |
| `OntologyConfig` | 2 | `IDocumentSourceFactory.cs`, `IConfigLoadService.cs` | split | the record stays; `IsRequired*` and the `With*` helpers that do work move |
| `PlanDocument` | 1 | `IArbiterPlanService.cs` | split | the record stays; `RedEvidenceRequirement` moves |
| `ReviewCommandResult` | 6 | `IReviewCatalogueService.cs`, `IReviewCycleService.cs`, `IReviewDuplicatesService.cs`, `IReviewGatesService.cs`, `IReviewSeatsService.cs`, `IReviewSuppressionsService.cs` | narrow | the record stays in Domain; `Refused` and `Reported` move to an Application factory |
| `ReviewPolicy` | 1 | `IReviewCycleSelector.cs` | split | the record stays; `FixRoundTakes` moves |
| `SeverityOverrides` | 2 | `ISeverityOverrideSource.cs`, `IReviewDecisionSteps.cs` | split | the record stays; `Find`, `Apply` and `RowsFor` move |
| `SquadDeploymentPlan` | 2 | `ISquadDeploymentPlanner.cs`, `ISquadTransaction.cs` | move the interface | `ISquadDeploymentPlanner` and `ISquadTransaction` move to Application with the plan |
| `SquadModelPreset` | 2 | `ISquadModelPresetStore.cs`, `ISquadModelPresetValidator.cs` | split | the record stays; `ApplyTo` moves |
| `SquadRenderRequest` | 1 | `ISquadRenderInputReader.cs` | split | the record stays; the `Require*` guards and `Unprepared` move |
| `StatusLineTargetRoots` | 1 | `IStatusLineDeploymentService.cs` | split | the record stays; the `Resolve*` helpers move |


Twenty of the twenty-two types are a record with a handful of helper methods, which is why split is the
common decision: the record is what `Contracts` needs and it stays exactly where it is.
`SquadDeploymentPlan` is the only type whose interface moves whole, because `ISquadDeploymentPlanner`
and `ISquadTransaction` exist for the deployment logic that phase 3 moves anyway. `ReviewCommandResult`
and `KyberWeaveConfigLoadResult` are the two where neither fits: their factory methods are the logic,
and they belong to six and two `Contracts` files, so the record stays and the factories go.

## Part 3: the move, in dependency order

One task per group, each a Domain folder or a slice of one, none over 30 logic classes, ordered so a
task's dependencies move before it where the graph allows. `depends on` names the folders whose logic
this group's code names; a cycle is harmless here because the whole move lands in one project and keeps
every namespace, so those tasks can run in either order.


| task | Domain group | logic classes | files | depends on | callers outside Application |
|---|---|---|---|---|---|
| 2.5.1 | `Configuration` | 23 | 23 | Arbiter: root, FileSystem, Review: catalogue and commands, Review: gates and cycles, Squad: deployment, briefs and the rest | KyberWeave.Application.Tests, KyberWeave.Arbiter, KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Mcp, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.2 | `Arbiter: Rules and providers` | 18 | 16 | Arbiter: Facts, Arbiter: root, FileSystem | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.3 | `Squad: deployment, briefs and the rest` | 24 | 23 | Configuration | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.RepoPolicy.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.4 | `Arbiter: root` | 16 | 12 | Arbiter: Facts, Arbiter: Rules and providers, Configuration, Squad: deployment, briefs and the rest | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Testing, KyberWeave.Tests |
| 2.5.5 | `Arbiter: Facts` | 26 | 23 | Arbiter: Rules and providers, Arbiter: root | KyberWeave.Cli, KyberWeave.Contracts, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.6 | `FileSystem` | 1 | 1 | none | KyberWeave.Data, KyberWeave.Tests |
| 2.5.7 | `Review: gates and cycles` | 15 | 14 | Arbiter: Rules and providers, Configuration, Review: duplicates and suppression, Review: seats and verdicts | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.8 | `Review: seats and verdicts` | 15 | 12 | Configuration, Review: duplicates and suppression, Review: gates and cycles | KyberWeave.Cli, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.9 | `Review: duplicates and suppression` | 5 | 4 | Arbiter: Rules and providers | KyberWeave.Contracts, KyberWeave.Core, KyberWeave.Data, KyberWeave.Integration.Tests, KyberWeave.Tests |
| 2.5.10 | `Security` | 2 | 2 | none | none |
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

The order is by how many other groups name a group's types, most-named first: the shared records and
helpers move before the code that uses them. The graph is cyclic at folder granularity (Arbiter's facts
name Squad models, and Squad's renderers name Arbiter rule types), so no order is a strict dependency
order; because every group lands in the same project and keeps its namespace, a cyclic pair can run in
either order without a compile break.

## Limits

- The scan matches type names, not resolved symbols. A name declared both in `Domain` and in another
  project would be counted as a caller, so a count may include one such collision; the file lists for
  the projects that need a route were reviewed and the route is a property of the folder, not of the
  single file.
- The scan classifies by declaration shape. A record whose only method is a real algorithm under a
  name in the trivial list (`Equals`, `ToString`) would be counted as data; none of the 20 read by hand
  was.
- A file with one data type and one logic type counts as one logic file, and the logic type count is
  per type, so the two totals do not add up to the file count.
- A dependency edge is a type name used in the group's code, so an edge can come from a doc comment
  that the scan kept; the edges were not read line by line.

