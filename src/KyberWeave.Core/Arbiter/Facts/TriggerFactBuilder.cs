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
/// Facts the dispatch input determines (caller, delegation, lens name,
/// roster and catalog projections, planning directories) carry values. Facts the
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

        if (fact.StartsWith("lens.name", StringComparison.Ordinal)
            || fact.StartsWith("finding.", StringComparison.Ordinal)
            || fact.StartsWith("gate.", StringComparison.Ordinal))
            return ArbiterFactLabel.Asserted;

        return ArbiterFactLabel.Derived;
    }

    /// <summary>Builds the fact bag for a classified dispatch.</summary>
    public static ArbiterFactSet Build(TriggerClassification classification, string prompt, KyberWeaveConfig config)
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
            config);
    }

    /// <summary>Builds the fact bag for <paramref name="trigger"/> from dispatch input.</summary>
    /// <exception cref="ArgumentException">Thrown for an unknown trigger, with the known names as hint.</exception>
    public static ArbiterFactSet Build(
        string trigger,
        string? target,
        string prompt,
        string? caller,
        string? callerSource,
        KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);

        if (!ArbiterTriggerCatalog.TryGetFacts(trigger, out IReadOnlySet<string>? declared))
        {
            throw new ArgumentException(
                $"Unknown Arbiter trigger '{trigger}'. Known triggers: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}.",
                nameof(trigger));
        }

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
                "delegation.plan-file" => planFile,
                "delegation.task" => task,
                "delegation.markers" => marker,
                "config.planning-dirs" => planningDirs,
                "lens.name" => lens,
                "lens.applicability" => applicability,
                "roster.caller.delegates-to" => delegatesTo,
                "roster.descriptions" => descriptions,
                _ => null,
            };

            facts = facts.With(name, value, LabelFor(name, callerSource));
        }

        return facts;
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

        return [
            DirectoryOf(Find(ConfigRegConfig.PlanIndexProperty)),
            DirectoryOf(Find(ConfigRegConfig.SpecificationIndexProperty)),
            DirectoryOf(Find(ConfigRegConfig.TodoIndexProperty)),
        ];
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
