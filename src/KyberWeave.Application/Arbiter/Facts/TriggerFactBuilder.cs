using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// Builds the labelled fact bag for a classified trigger (design section 1.3):
/// every fact <see cref="ArbiterTriggerCatalog"/> declares for the trigger, with
/// the label its source table fixes. Plan and task identity come from
/// <c>PLAN_FILE</c> and <c>TASK</c> only; nothing is inferred from the plan index.
/// </summary>
/// <remarks>
/// Facts the dispatch input determines (caller, delegation, lens name, the finding
/// a refutation carries, roster and catalog projections, planning directories) carry values. Facts the
/// evaluation computes later (plan projections, ledger, git, review diffs, file
/// reads, step-0 answers) are present with a null value so the bag's shape always
/// matches the catalog row; the step-0 answer pattern <c>rules.&lt;id&gt;.answer</c>
/// is the one exception and is never materialised without a rule id.
/// </remarks>
public static class TriggerFactBuilder
{
    /// <summary>
    /// True when every file the task names is a test file, read from <c>plan.task.files</c> and the
    /// host's test-file patterns.
    /// </summary>
    public const string FilesAllTestsFact = "plan.task.files-all-tests";

    private const string TaskGatesDeclaredFact = "config.task-gates-declared";

    /// <summary>How many lines either side of a refuted finding's line an earlier finding may sit and still be a candidate.</summary>
    private const int PriorFindingWindowLines = 20;

    /// <summary>The most earlier findings one refutation compares against.</summary>
    private const int MaxPriorFindings = 3;

    /// <summary>
    /// Returns the provenance label for <paramref name="fact"/>: <c>caller</c> is
    /// asserted only when inferred from headers; <c>delegation.*</c> (except the
    /// catalog-projected class and profile), <c>lens.name</c>, <c>finding.*</c> and
    /// <c>gate.*</c> are asserted dispatch or lens output; everything else is derived.
    /// </summary>
    public static ArbiterFactLabel LabelFor(string fact, string? callerSource)
    {
        ArgumentNullException.ThrowIfNull(fact);

        if (string.Equals(fact, "caller", StringComparison.Ordinal))
        {
            return string.Equals(callerSource, ArbiterCallerSources.Header, StringComparison.Ordinal)
                ? ArbiterFactLabel.Asserted
                : ArbiterFactLabel.Derived;
        }

        if (fact.StartsWith("delegation.", StringComparison.Ordinal)
            && !string.Equals(fact, "delegation.target-class", StringComparison.Ordinal)
            && !string.Equals(fact, "delegation.target-profile", StringComparison.Ordinal))
            return ArbiterFactLabel.Asserted;

        // The gate facts the Arbiter computes rather than receives: they join a returned
        // digest to the reports on disk, so they are derived even though gate.* is usually
        // dispatch or lens output.
        if (string.Equals(fact, GateReportFacts.AgreesFact, StringComparison.Ordinal)
            || string.Equals(fact, GateReportFacts.FailingRunFirstFact, StringComparison.Ordinal))
            return ArbiterFactLabel.Derived;

        // Measured by the hook from the changed hunks, not supplied by the lens that returned the finding.
        if (string.Equals(fact, "finding.changed-lines", StringComparison.Ordinal))
            return ArbiterFactLabel.Derived;

        if (fact.StartsWith("lens.name", StringComparison.Ordinal)
            || fact.StartsWith("finding.", StringComparison.Ordinal)
            || fact.StartsWith("gate.", StringComparison.Ordinal))
            return ArbiterFactLabel.Asserted;

        return ArbiterFactLabel.Derived;
    }

    /// <summary>Builds the fact bag for a classified dispatch.</summary>
    /// <param name="classification">The classified dispatch.</param>
    /// <param name="prompt">The dispatch prompt, header block included.</param>
    /// <param name="config">The host configuration.</param>
    /// <param name="observesReturns">Whether the host's harness observes sub-agent
    /// returns (task 16.9): the value of <c>harness.observes-returns</c>. The host
    /// always supplies it — true for every harness except the ones whose adapter
    /// declares otherwise — so a missing value can never silently disable an
    /// enforced rule.</param>
    /// <param name="modelOverrideField">The dispatch field that carries a sub-agent's model, or null when
    /// the harness offers no override: the value of <c>harness.model-override</c>.</param>
    /// <param name="delegationModel">The model the dispatch input names for its sub-agent, or null when it
    /// names none: the value of <c>delegation.model</c>.</param>
    /// <param name="secondaryTriggers">The secondary triggers the primary dispatch carries. Their declared facts
    /// join the bag, because the secondary rules read from the same bag as the primary.</param>
    /// <param name="promptInserted">Whether the hook inserted the prompt into this dispatch. Only that evidence,
    /// never the prompt's own text, exempts it from the plan-read rule: a conductor can type the tags itself.</param>
    /// <param name="repoRoot">The repository root the standards folders sit under, when the caller has one. It
    /// resolves the technology's parent template, so a parent-keyed gate is found exactly as the gate run finds it.</param>
    /// <param name="files">The file system the standards are read through; the parent technology is resolved only when it and <paramref name="repoRoot"/> are both given.</param>
    public static ArbiterFactSet Build(
        TriggerClassification classification,
        string prompt,
        KyberWeaveConfig config,
        bool observesReturns = true,
        string? modelOverrideField = null,
        string? delegationModel = null,
        IReadOnlyList<string>? secondaryTriggers = null,
        bool promptInserted = false,
        string? repoRoot = null,
        IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);

        if (classification.Trigger is null)
            return new ArbiterFactSet();

        return Build(
            classification.Trigger,
            classification.Target,
            prompt,
            classification.Caller,
            classification.CallerSource,
            config,
            observesReturns,
            modelOverrideField,
            delegationModel,
            secondaryTriggers,
            promptInserted,
            files is null ? null : repoRoot,
            files);
    }

    /// <summary>Builds the fact bag for <paramref name="trigger"/> from dispatch input.</summary>
    /// <param name="trigger">The trigger the dispatch classified as.</param>
    /// <param name="target">The dispatch target, when there is one.</param>
    /// <param name="prompt">The dispatch prompt, header block included.</param>
    /// <param name="caller">The calling agent, when classified.</param>
    /// <param name="callerSource">How the caller was classified.</param>
    /// <param name="config">The host configuration.</param>
    /// <param name="observesReturns">Whether the harness observes sub-agent returns: the value of <c>harness.observes-returns</c>.</param>
    /// <param name="modelOverrideField">The dispatch field that carries a sub-agent's model, or null when the harness offers no override.</param>
    /// <param name="delegationModel">The model the dispatch input names for its sub-agent, or null.</param>
    /// <param name="secondaryTriggers">Secondary triggers whose declared facts are added to the bag.</param>
    /// <param name="promptInserted">Whether the hook inserted the prompt into this dispatch.</param>
    /// <param name="repoRoot">The repository root the standards folders sit under, when the caller has one.</param>
    /// <param name="files">The file system the standards are read through; required when <paramref name="repoRoot"/> is given.</param>
    /// <exception cref="ArgumentException">Thrown for an unknown trigger, with the known names as hint.</exception>
    public static ArbiterFactSet Build(
        string trigger,
        string? target,
        string prompt,
        string? caller,
        string? callerSource,
        KyberWeaveConfig config,
        bool observesReturns = true,
        string? modelOverrideField = null,
        string? delegationModel = null,
        IReadOnlyList<string>? secondaryTriggers = null,
        bool promptInserted = false,
        string? repoRoot = null,
        IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);
        if (repoRoot is not null)
            ArgumentNullException.ThrowIfNull(files);

        if (!ArbiterTriggerCatalog.TryGetFacts(trigger, out IReadOnlySet<string>? primaryDeclared))
        {
            throw new ArgumentException(
                $"Unknown Arbiter trigger '{trigger}'. Known triggers: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}.",
                nameof(trigger));
        }

        IReadOnlySet<string> declared = DeclaredFacts(primaryDeclared, secondaryTriggers);

        HeaderBlock block = HeaderBlock.Parse(prompt);
        string stripped = HeaderBlock.Strip(prompt);
        IReadOnlyList<string> paths = ArbiterPathRule.ExtractPaths(stripped);
        string targetClass = TriggerClassifier.TargetClassFor(target);
        string? marker = TriggerClassifier.PlannerMarkerFor(prompt, block.Headers);

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        string? profile = catalog.Agents.TryGetValue(target ?? string.Empty, out ArbiterSquadAgent? agent)
            ? agent.CapabilityProfile
            : null;
        IReadOnlyList<string> delegatesTo = caller is not null
                                            && catalog.Agents.TryGetValue(caller, out ArbiterSquadAgent? callerAgent)
            ? RosterDelegatesTo.For(callerAgent, config.WorkerRoster, catalog)
            : [];
        Dictionary<string, string> descriptions = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ArbiterSquadAgent> entry in catalog.Agents)
            descriptions[entry.Key] = entry.Value.Description;

        block.Headers.TryGetValue(DispatchHeaders.PlanFile, out string? planFile);
        block.Headers.TryGetValue(DispatchHeaders.Task, out string? task);
        block.Headers.TryGetValue(DispatchHeaders.Technology, out string? technology);
        string? parentTechnology = ParentTechnology(files, repoRoot, config, technology);
        block.Headers.TryGetValue(DispatchHeaders.Lens, out string? lens);
        block.Headers.TryGetValue(DispatchHeaders.Refute, out string? refute);
        // REFUTE names the finding as <lens>/<slug>. The finding block's id line carries either that full value
        // or the bare slug, so the block is read by the full value first and the slug second.
        RefuteFinding? finding = refute is not null && string.Equals(trigger, ArbiterTriggers.RefuteSpawn, StringComparison.Ordinal)
            ? RefuteFinding.Parse(stripped, refute) ?? RefuteFinding.Parse(stripped, refute[(refute.LastIndexOf('/') + 1)..])
            : null;
        string? applicability = lens is not null
                                && catalog.Lenses.TryGetValue(lens, out ArbiterSquadLens? lensEntry)
            ? lensEntry.Applicability
            : null;
        IReadOnlyList<string> planningDirs = PlanningDirs(config);

        ArbiterFactSet facts = new();
        foreach (string name in declared)
        {
            if (ArbiterTriggerCatalog.IsAnswerRef(name))
                continue;

            object? value = name switch
            {
                "caller" => caller ?? "unidentified",
                "delegation.target" => target,
                "delegation.target-class" => targetClass,
                "delegation.target-profile" => profile,
                ArbiterTriggerCatalog.DelegationPromptFact => stripped,
                // Bounded excerpts for model questions: the raw head keeps the header block as written, the body
                // head is the dispatch the worker reads. Raw header values keep what validation rejected.
                "delegation.head" => TextExcerpts.Head(prompt),
                "delegation.body-head" => TextExcerpts.Head(PlanReadInstruction.WithoutRoutingMetadata(stripped)),
                "delegation.plan-file-raw" => block.RawValues.TryGetValue(DispatchHeaders.PlanFile, out string? rawPlan) ? rawPlan : null,
                "delegation.task-raw" => block.RawValues.TryGetValue(DispatchHeaders.Task, out string? rawTask) ? rawTask : null,
                // The shortcut rules' questions read the dispatch body: the defect the investigator was sent to
                // find, and the intake request the planner was asked to assess.
                "defect.report" => stripped,
                "intake.request" => stripped,
                "delegation.paths" => paths,
                // The plan-read rule's question, asked by the one detector the docs
                // validator also uses, so a spelling the rule accepts is a spelling
                // KW-DOC-TASKS-005 accepts. A dispatch the hook expanded is never a plan
                // read: its host facts name the planning indexes, and the worker is told to
                // use that prompt, not a plan (Req 9.18). Only the hook's own record of the
                // insertion exempts it, because a conductor can type the prompt's tags too.
                "delegation.planning-paths" => PlanningPaths(promptInserted, stripped, planningDirs),
                "delegation.plan-file" => planFile,
                // Absent (null) unless PLAN_FILE was present but failed validation or repeated.
                "delegation.plan-file-rejected" => RejectedFlag(block),
                "delegation.task" => task,
                "delegation.markers" => marker,
                "config.planning-dirs" => planningDirs,
                // A machine fact: whether the host's task gates resolve any gate for the agent that returned and
                // the technology the dispatch names, by the same resolver the gate run uses. A host whose gates
                // are keyed only by technology must not read as having none.
                "config.task-gates-declared" => TaskGatesDeclared(config, target, technology, parentTechnology),
                "lens.name" => lens,
                "lens.applicability" => applicability,
                "finding.id" => finding is null ? null : refute,
                "finding.claim" => finding?.Claim,
                "finding.excerpt" => finding?.Excerpt,
                "finding.file" => finding?.File,
                "finding.line" => finding?.Line,
                "roster.caller.delegates-to" => delegatesTo,
                "roster.descriptions" => descriptions,
                "harness.observes-returns" => observesReturns,
                "harness.model-override" => modelOverrideField,
                "delegation.model" => delegationModel,
                _ => null
            };

            facts = facts.With(name, value, LabelFor(name, callerSource));
        }

        return facts;
    }

    /// <summary>The technology the dispatched technology builds on, when a root and the files to read both resolve.</summary>
    private static string? ParentTechnology(
        IFileSystem? files, string? repoRoot, KyberWeaveConfig config, string? technology) =>
        repoRoot is not null && files is not null && technology is not null
            ? TaskGateResolver.ParentTechnologyOrNone(files, repoRoot, config, technology)
            : null;

    /// <summary>The planning paths a dispatch names, unless the hook inserted the prompt and so already routed it.</summary>
    private static IReadOnlyList<string> PlanningPaths(bool promptInserted, string stripped, IReadOnlyList<string> planningDirs) =>
        promptInserted
            ? []
            : PlanReadInstruction.NamesPathUnder(PlanReadInstruction.WithoutRoutingMetadata(stripped), planningDirs);

    /// <summary>True when PLAN_FILE was present but failed validation or was repeated, otherwise null.</summary>
    private static bool? RejectedFlag(HeaderBlock block) =>
        block.RejectedNames.Contains(DispatchHeaders.PlanFile) ? true : null;

    /// <summary>Whether the task's gates resolve any gate for the returning agent and technology.</summary>
    private static bool TaskGatesDeclared(
        KyberWeaveConfig config,
        string? target,
        string? technology,
        string? parentTechnology) =>
        target is not null
        && TaskGateResolver.Resolve(config.Review, target, technology, parentTechnology).Gates.Count > 0;

    /// <summary>
    /// Narrows <c>config.task-gates-declared</c> to the gates that apply to the task's files (D10): the fact stays
    /// true only when a resolved gate has no include, or an include that matches one of <c>plan.task.files</c>.
    /// </summary>
    /// <remarks>
    /// The plan reader adds <c>plan.task.files</c> after the static build, so this runs after it. A task whose
    /// files are not known keeps the fact as built: files nobody listed are not a claim that no gate applies. A
    /// gate that selects none of the task's files (the docs developer's gate selects only the host configuration)
    /// is a gate the task owes nothing to, so the rule's existing no-gates-declared answer allows the return.
    /// </remarks>
    /// <param name="facts">The bag from the static build and the plan reader.</param>
    /// <param name="config">The host configuration whose gates resolve.</param>
    /// <param name="target">The agent that returned.</param>
    /// <param name="prompt">The dispatch prompt, whose header names the technology.</param>
    /// <param name="parentTechnology">The technology the dispatched one builds on, when the caller resolved it.</param>
    public static ArbiterFactSet WithTaskGatesApplyFact(
        ArbiterFactSet facts, KyberWeaveConfig config, string? target, string prompt, string? parentTechnology = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(prompt);

        if (target is null
            || !facts.TryGet(TaskGatesDeclaredFact, out ArbiterFact? declared)
            || declared.Value is not true
            || !facts.TryGet("plan.task.files", out ArbiterFact? files)
            || files.Value is not IEnumerable<string> paths)
        {
            return facts;
        }

        List<string> taskFiles = [.. paths];
        if (taskFiles.Count == 0)
            return facts;

        HeaderBlock.Parse(prompt).Headers.TryGetValue(DispatchHeaders.Technology, out string? technology);
        IReadOnlyList<ReviewTaskGate> gates = TaskGateResolver.Resolve(config.Review, target, technology, parentTechnology).Gates;
        bool applies = gates.Any(gate => gate.Include.Count == 0
                                         || taskFiles.Any(path => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path))));
        return applies ? facts : facts.With(TaskGatesDeclaredFact, false, ArbiterFactLabel.Derived);
    }

    /// <summary>
    /// Adds <see cref="FilesAllTestsFact"/> when the task names files: true when every one matches
    /// the host's test-file patterns, false when one does not.
    /// </summary>
    /// <remarks>
    /// A task that names no files leaves the fact absent rather than vacuously true: a task with no
    /// files is a task the plan described without a scope, and exempting it from the test-first check
    /// would be a claim about files nobody listed. The rule then decides on its other clauses.
    /// </remarks>
    public static ArbiterFactSet WithTestsOnlyTaskFact(
        ArbiterFactSet facts,
        IReadOnlyList<string> testFilePatterns)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(testFilePatterns);

        if (!facts.Contains(FilesAllTestsFact)
            || !facts.TryGet("plan.task.files", out ArbiterFact? files)
            || files.Value is not IEnumerable<string> paths)
        {
            return facts;
        }

        List<string> declared = [.. paths];
        if (declared.Count == 0)
            return facts;

        bool allTests = declared.All(path => testFilePatterns.Any(pattern => PathGlob.IsMatch(pattern, path)));
        return facts.With(FilesAllTestsFact, allTests, ArbiterFactLabel.Derived);
    }

    /// <summary>
    /// Adds a lens return's finding facts: its id, its text, its stated severity, and whether a failed gate
    /// corroborates it. Built from the return, not the dispatch, so it is added after the static build.
    /// </summary>
    public static ArbiterFactSet WithReturnedFinding(ArbiterFactSet facts, ReturnedFinding finding, bool gateCorroborated)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(finding);

        ArbiterFactSet bag = facts
            .With("finding.id", finding.Id, ArbiterFactLabel.Asserted)
            .With("finding.excerpt", finding.Text, ArbiterFactLabel.Asserted)
            .With("finding.gate-corroborated", gateCorroborated, ArbiterFactLabel.Derived);
        return finding.Severity is null
            ? bag
            : bag.With("finding.severity", finding.Severity, ArbiterFactLabel.Asserted);
    }

    /// <summary>
    /// Adds <c>finding.dropped</c> for a refuted finding, read from the review store's items for its run. A
    /// finding with no stored item keeps the fact absent, so a refuter cannot be denied on no evidence.
    /// </summary>
    /// <param name="facts">The fact bag to extend.</param>
    /// <param name="findingId">The refuted finding's id.</param>
    /// <param name="storedItems">The review run's stored items, in append order; the latest for the id decides.</param>
    public static ArbiterFactSet WithDroppedStatus(ArbiterFactSet facts, string findingId, IReadOnlyList<string> storedItems)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(findingId);
        ArgumentNullException.ThrowIfNull(storedItems);

        string? status = null;
        string key = ReturnedFinding.IdKey(findingId);
        foreach (string item in storedItems)
        {
            IReadOnlyList<ReturnedFinding> stored = ReturnedFinding.ParseAll(item);
            if (stored.Count > 0 && string.Equals(ReturnedFinding.IdKey(stored[0].Id), key, StringComparison.Ordinal))
                status = StatusOf(item) ?? status;
        }

        return status is null
            ? facts
            : facts.With("finding.dropped", string.Equals(status, ReturnedFinding.DroppedStatus, StringComparison.Ordinal), ArbiterFactLabel.Derived);
    }

    /// <summary>
    /// Adds <c>review.prior-findings</c>: the earlier findings of this review that name the refuted finding's
    /// file and sit within 20 lines of its line, oldest first, at most three. Only findings stored before the
    /// refuted finding's own item are candidates, so a finding is never compared against itself.
    /// </summary>
    /// <param name="facts">The fact bag to extend; it must already carry <c>finding.file</c> and <c>finding.line</c>.</param>
    /// <param name="findingId">The refuted finding's id, bare or lens-qualified; both sides are compared by slug.</param>
    /// <param name="storedItems">The review run's stored items, in append order.</param>
    /// <remarks>
    /// With no file or line for the finding the neighbours are undecidable, so the fact is left absent rather than
    /// written as an empty list: an empty list would read as "no neighbours" and let a rule decide on nothing.
    /// </remarks>
    public static ArbiterFactSet WithPriorFindings(ArbiterFactSet facts, string findingId, IReadOnlyList<string> storedItems)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(findingId);
        ArgumentNullException.ThrowIfNull(storedItems);

        string? file = facts.TryGet("finding.file", out ArbiterFact? fileFact) ? fileFact.Value as string : null;
        int? line = facts.TryGet("finding.line", out ArbiterFact? lineFact) ? lineFact.Value as int? : null;
        if (file is null || line is null)
            return facts;

        string key = ReturnedFinding.IdKey(findingId);
        List<string> prior = [];
        foreach (string item in storedItems)
        {
            IReadOnlyList<ReturnedFinding> parsed = ReturnedFinding.ParseAll(item);
            if (parsed.Count == 0)
                continue;
            if (string.Equals(ReturnedFinding.IdKey(parsed[0].Id), key, StringComparison.Ordinal))
                break;
            if (!string.Equals(FieldOf(item, "file"), file, StringComparison.Ordinal))
                continue;
            if (int.TryParse(FieldOf(item, "line"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int otherLine)
                && Math.Abs(otherLine - line.Value) <= PriorFindingWindowLines)
            {
                prior.Add(item);
                if (prior.Count == MaxPriorFindings)
                    break;
            }
        }

        return facts.With("review.prior-findings", prior, ArbiterFactLabel.Derived);
    }

    /// <summary>The value of a <c>name:</c> line in a stored finding block, or null when it has none.</summary>
    private static string? FieldOf(string item, string name)
    {
        string prefix = name + ":";
        foreach (string raw in item.Split('\n'))
        {
            string line = raw.Trim().TrimStart('-').TrimStart();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    /// <summary>The status a stored item carries on its last <c>status:</c> line, or null when it has none.</summary>
    private static string? StatusOf(string item)
    {
        string? status = null;
        foreach (string line in item.Split('\n'))
        {
            if (line.StartsWith(ReturnedFinding.StatusPrefix, StringComparison.Ordinal))
                status = line[ReturnedFinding.StatusPrefix.Length..].Trim();
        }

        return status;
    }

    /// <summary>The primary trigger's facts plus every secondary trigger's, so one bag serves them all.</summary>
    private static IReadOnlySet<string> DeclaredFacts(IReadOnlySet<string> primary, IReadOnlyList<string>? secondaryTriggers)
    {
        if (secondaryTriggers is null || secondaryTriggers.Count == 0)
            return primary;

        HashSet<string> union = new(primary, StringComparer.Ordinal);
        foreach (string secondary in secondaryTriggers)
        {
            if (ArbiterTriggerCatalog.TryGetFacts(secondary, out IReadOnlySet<string>? secondaryDeclared))
                union.UnionWith(secondaryDeclared);
        }

        return union;
    }

    private static IReadOnlyList<string> PlanningDirs(KyberWeaveConfig config)
    {
        IReadOnlyList<ConfigRegEntry> entries = config.ConfigReg.Resolve(config.Ontology);

        string? Find(string name)
        {
            foreach (ConfigRegEntry entry in entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                    return entry.Path;
            }

            return null;
        }

        return
        [
            PlanningGlobOf(Find(ConfigRegConfig.PlanIndexProperty)),
            PlanningGlobOf(Find(ConfigRegConfig.SpecificationIndexProperty)),
            PlanningGlobOf(Find(ConfigRegConfig.TodoIndexProperty))
        ];
    }

    /// <summary>
    /// The index's directory as a directory-prefix glob, so a rule matches a plan file
    /// beneath it rather than only the bare directory. An unresolved index keeps the
    /// empty result: a host with no planning directories offers nothing to match.
    /// </summary>
    private static string PlanningGlobOf(string? indexPath)
    {
        string directory = DirectoryOf(indexPath);
        return directory.Length == 0 ? string.Empty : directory + "/**";
    }

    private static string DirectoryOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized.Substring(0, slash);
    }
}
