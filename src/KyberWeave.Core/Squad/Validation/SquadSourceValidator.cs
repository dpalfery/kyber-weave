using System.Diagnostics.CodeAnalysis;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Validation;

/// <summary>Cross-document validation for canonical Squad source.</summary>
public static class SquadSourceValidator
{
    /// <summary>Stable rule id for an invalid canonical Squad source document.</summary>
    public const string InvalidSourceRule = "KW-SQUAD-SOURCE-001";

    /// <summary>Validates identities, aliases, profile references, and bundle membership.</summary>
    public static void Validate(
        SquadBundle bundle,
        IReadOnlyList<SquadAgent> agents,
        IReadOnlyList<SquadSkill> skills,
        SquadModelProfiles models,
        SquadCapabilityProfiles capabilities,
        SquadFallbackProfiles fallbacks)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(fallbacks);

        ValidateDuplicateAgents(agents);
        ValidateDuplicateSkills(skills);
        ValidateAliases(agents);
        ValidateReservedFallbackPrefixes(agents, skills, fallbacks);

        HashSet<string> agentNames = agents.Select(agent => agent.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> skillNames = skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> aliases = agents.SelectMany(agent => agent.Aliases).ToHashSet(StringComparer.Ordinal);
        ValidateFallbackIdentities(agents, skills, fallbacks);

        foreach (SquadAgent agent in agents)
        {
            if (!models.Profiles.ContainsKey(agent.ModelProfile))
            {
                Throw(
                    $"Agent '{agent.Name}' references unknown model profile '{agent.ModelProfile}'.",
                    agent.Name,
                    agent.SourcePath,
                    "Use a model-profile declared in profiles/models.yml.");
            }

            if (!capabilities.Profiles.ContainsKey(agent.CapabilityProfile))
            {
                Throw(
                    $"Agent '{agent.Name}' references unknown capability profile '{agent.CapabilityProfile}'.",
                    agent.Name,
                    agent.SourcePath,
                    "Use a capability-profile declared in profiles/capabilities.yml.");
            }

            if (!fallbacks.Profiles.ContainsKey(agent.Fallback))
            {
                Throw(
                    $"Agent '{agent.Name}' references unknown fallback profile '{agent.Fallback}'.",
                    agent.Name,
                    agent.SourcePath,
                    "Use a fallback declared in profiles/fallbacks.yml.");
            }

            foreach (string delegateName in agent.DelegatesTo)
            {
                if (!agentNames.Contains(delegateName))
                {
                    Throw(
                        $"Agent '{agent.Name}' delegates to unknown agent '{delegateName}'.",
                        agent.Name,
                        agent.SourcePath,
                        "Use the canonical name of an agent present under agents/.");
                }
            }
        }

        ValidateDistinctBundleEntries(bundle.AgentNames, "agent", bundle.SourcePath);
        ValidateDistinctBundleEntries(bundle.SkillNames, "skill", bundle.SourcePath);

        foreach (string name in bundle.AgentNames)
        {
            if (agentNames.Contains(name))
            {
                continue;
            }

            string hint = aliases.Contains(name)
                ? "Bundle entries must use the agent's canonical name rather than an alias."
                : "Add the canonical agent under agents/ or remove it from this bundle.";
            Throw(
                $"Bundle '{bundle.Name}' references unknown agent '{name}'.",
                bundle.Name,
                bundle.SourcePath,
                hint);
        }

        foreach (string name in bundle.SkillNames)
        {
            if (!skillNames.Contains(name))
            {
                Throw(
                    $"Bundle '{bundle.Name}' references unknown skill '{name}'.",
                    bundle.Name,
                    bundle.SourcePath,
                    "Add the canonical skill under skills/ or remove it from this bundle.");
            }
        }
    }

    [DoesNotReturn]
    internal static void Throw(
        string message,
        string subject,
        string filePath,
        string hint,
        int? startLine = null,
        IReadOnlyList<DiagnosticLocation>? relatedLocations = null)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(new Diagnostic(
            InvalidSourceRule,
            Severity.Error,
            message,
            subject,
            filePath,
            hint,
            startLine,
            startLine,
            relatedLocations));
        throw new SquadSourceValidationException(report);
    }

    private static void ValidateDuplicateAgents(IReadOnlyList<SquadAgent> agents)
    {
        Dictionary<string, SquadAgent> seen = new Dictionary<string, SquadAgent>(StringComparer.Ordinal);
        foreach (SquadAgent agent in agents)
        {
            if (seen.TryGetValue(agent.Name, out SquadAgent? previous))
            {
                Throw(
                    $"Agent identity '{agent.Name}' is duplicate.",
                    agent.Name,
                    agent.SourcePath,
                    "Give every agent a unique canonical name.",
                    relatedLocations:
                    [new DiagnosticLocation(previous.SourcePath, Message: "First declaration.")]);
            }

            seen.Add(agent.Name, agent);
        }
    }

    private static void ValidateDuplicateSkills(IReadOnlyList<SquadSkill> skills)
    {
        Dictionary<string, SquadSkill> seen = new Dictionary<string, SquadSkill>(StringComparer.Ordinal);
        foreach (SquadSkill skill in skills)
        {
            if (seen.TryGetValue(skill.Name, out SquadSkill? previous))
            {
                Throw(
                    $"Skill identity '{skill.Name}' is duplicate.",
                    skill.Name,
                    skill.SourcePath,
                    "Give every skill a unique canonical name.",
                    relatedLocations:
                    [new DiagnosticLocation(previous.SourcePath, Message: "First declaration.")]);
            }

            seen.Add(skill.Name, skill);
        }
    }

    private static void ValidateAliases(IReadOnlyList<SquadAgent> agents)
    {
        Dictionary<string, SquadAgent> canonical = agents.ToDictionary(agent => agent.Name, StringComparer.Ordinal);
        Dictionary<string, SquadAgent> aliases = new Dictionary<string, SquadAgent>(StringComparer.Ordinal);

        foreach (SquadAgent agent in agents)
        {
            foreach (string alias in agent.Aliases)
            {
                if (canonical.TryGetValue(alias, out SquadAgent? canonicalOwner))
                {
                    Throw(
                        $"Agent alias '{alias}' collides with canonical agent '{canonicalOwner.Name}'.",
                        agent.Name,
                        agent.SourcePath,
                        "Choose an alias that is not an agent's canonical name.",
                        relatedLocations:
                        [new DiagnosticLocation(canonicalOwner.SourcePath, Message: "Canonical declaration.")]);
                }

                if (aliases.TryGetValue(alias, out SquadAgent? aliasOwner))
                {
                    Throw(
                        $"Agent alias '{alias}' is already declared by '{aliasOwner.Name}'.",
                        agent.Name,
                        agent.SourcePath,
                        "Choose a unique alias or remove the duplicate alias.",
                        relatedLocations:
                        [new DiagnosticLocation(aliasOwner.SourcePath, Message: "First alias declaration.")]);
                }

                aliases.Add(alias, agent);
            }
        }
    }

    private static void ValidateReservedFallbackPrefixes(
        IReadOnlyList<SquadAgent> agents,
        IReadOnlyList<SquadSkill> skills,
        SquadFallbackProfiles fallbacks)
    {
        foreach ((string? profileName, SquadFallbackProfile? profile) in fallbacks.Profiles)
        {
            string prefix = profile.OutputIdentity.Prefix;
            DiagnosticLocation relatedLocation = new DiagnosticLocation(
                fallbacks.SourcePath,
                Message: $"Fallback profile '{profileName}' reserves prefix '{prefix}'.");

            foreach (SquadAgent agent in agents)
            {
                ValidateIdentity(agent.Name, "Agent", agent.SourcePath, prefix, relatedLocation);
                foreach (string alias in agent.Aliases)
                {
                    ValidateIdentity(alias, "Agent alias", agent.SourcePath, prefix, relatedLocation);
                }
            }

            foreach (SquadSkill skill in skills)
            {
                ValidateIdentity(skill.Name, "Skill", skill.SourcePath, prefix, relatedLocation);
            }
        }

        static void ValidateIdentity(
            string identity,
            string kind,
            string sourcePath,
            string prefix,
            DiagnosticLocation relatedLocation)
        {
            if (!identity.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }

            Throw(
                $"{kind} identity '{identity}' uses reserved fallback prefix '{prefix}'.",
                identity,
                sourcePath,
                $"Rename '{identity}' so it does not begin with the reserved prefix '{prefix}'.",
                relatedLocations: [relatedLocation]);
        }
    }

    private static void ValidateFallbackIdentities(
        IReadOnlyList<SquadAgent> agents,
        IReadOnlyList<SquadSkill> skills,
        SquadFallbackProfiles fallbacks)
    {
        Dictionary<string, SquadAgent> agentsByName = agents.ToDictionary(agent => agent.Name, StringComparer.Ordinal);
        Dictionary<string, SquadSkill> skillsByName = skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);

        foreach ((string? profileName, SquadFallbackProfile? profile) in fallbacks.Profiles)
        {
            HashSet<string> shared = profile.SharedIdentities.ToHashSet(StringComparer.Ordinal);
            foreach (string name in shared)
            {
                if (!agentsByName.TryGetValue(name, out SquadAgent? agent))
                {
                    Throw(
                        $"Fallback profile '{profileName}' declares shared identity '{name}' without both an agent and skill.",
                        name,
                        fallbacks.SourcePath,
                        "Declare the same canonical identity as both an agent and a skill, or remove it from shared-identities.");
                }

                if (!skillsByName.TryGetValue(name, out SquadSkill? skill))
                {
                    Throw(
                        $"Fallback profile '{profileName}' declares shared identity '{name}' without both an agent and skill.",
                        name,
                        fallbacks.SourcePath,
                        "Declare the same canonical identity as both an agent and a skill, or remove it from shared-identities.");
                }

                if (!string.Equals(agent.InstructionBody, skill.InstructionBody, StringComparison.Ordinal))
                {
                    Throw(
                        $"Fallback profile '{profileName}' declares shared identity '{name}' with different instruction bodies.",
                        name,
                        fallbacks.SourcePath,
                        "Use the canonical agent body as the same-name skill body.",
                        relatedLocations:
                        [
                            new DiagnosticLocation(agent.SourcePath, Message: "Canonical agent body."),
                            new DiagnosticLocation(skill.SourcePath, Message: "Same-name skill body.")
                        ]);
                }
            }

            foreach (SquadAgent agent in agents)
            {
                if (!skillsByName.ContainsKey(agent.Name) || shared.Contains(agent.Name))
                {
                    continue;
                }

                string projectedName = $"{profile.OutputIdentity.Prefix}{agent.Name}";
                if (skillsByName.TryGetValue(projectedName, out SquadSkill? collidingSkill))
                {
                    Throw(
                        $"Fallback projection '{projectedName}' for agent '{agent.Name}' collides with a canonical skill.",
                        agent.Name,
                        fallbacks.SourcePath,
                        "Rename the canonical skill or choose a non-colliding fallback prefix.",
                        relatedLocations:
                        [new DiagnosticLocation(collidingSkill.SourcePath, Message: "Colliding skill.")]);
                }
            }
        }
    }

    private static void ValidateDistinctBundleEntries(
        IReadOnlyList<string> entries,
        string kind,
        string sourcePath)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            if (!seen.Add(entry))
            {
                Throw(
                    $"Bundle contains duplicate {kind} entry '{entry}'.",
                    entry,
                    sourcePath,
                    $"Remove the duplicate {kind} entry.");
            }
        }
    }
}
