using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;

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

        // The one gate fact the Arbiter computes rather than receives: it joins a returned
        // digest to the report it names, so it is derived even though gate.* is usually
        // dispatch or lens output.
        if (string.Equals(fact, GateReportFacts.AgreesFact, StringComparison.Ordinal))
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
    public static ArbiterFactSet Build(
        TriggerClassification classification,
        string prompt,
        KyberWeaveConfig config,
        bool observesReturns = true,
        string? modelOverrideField = null,
        string? delegationModel = null,
        IReadOnlyList<string>? secondaryTriggers = null)
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
            secondaryTriggers);
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
        IReadOnlyList<string>? secondaryTriggers = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);

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
            ? callerAgent.DelegatesTo
            : [];
        Dictionary<string, string> descriptions = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ArbiterSquadAgent> entry in catalog.Agents)
            descriptions[entry.Key] = entry.Value.Description;

        block.Headers.TryGetValue("PLAN_FILE", out string? planFile);
        block.Headers.TryGetValue("TASK", out string? task);
        block.Headers.TryGetValue("LENS", out string? lens);
        block.Headers.TryGetValue("REFUTE", out string? refute);
        RefuteFinding? finding = refute is not null && string.Equals(trigger, "refute.spawn", StringComparison.Ordinal)
            ? RefuteFinding.Parse(stripped, refute)
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
                "delegation.prompt" => stripped,
                "delegation.paths" => paths,
                // The plan-read rule's question, asked by the one detector the docs
                // validator also uses, so a spelling the rule accepts is a spelling
                // KW-DOC-TASKS-005 accepts.
                "delegation.planning-paths" => PlanReadInstruction.NamesPathUnder(stripped, planningDirs),
                "delegation.plan-file" => planFile,
                // Absent (null) unless PLAN_FILE was present but failed validation or repeated.
                "delegation.plan-file-rejected" => block.RejectedNames.Contains("PLAN_FILE") ? true : null,
                "delegation.task" => task,
                "delegation.markers" => marker,
                "config.planning-dirs" => planningDirs,
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
        foreach (string item in storedItems)
        {
            IReadOnlyList<ReturnedFinding> stored = ReturnedFinding.ParseAll(item);
            if (stored.Count > 0 && string.Equals(stored[0].Id, findingId, StringComparison.Ordinal))
                status = StatusOf(item) ?? status;
        }

        return status is null
            ? facts
            : facts.With("finding.dropped", string.Equals(status, ReturnedFinding.DroppedStatus, StringComparison.Ordinal), ArbiterFactLabel.Derived);
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
