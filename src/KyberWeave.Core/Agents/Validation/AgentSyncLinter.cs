using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Agents.Validation;

/// <summary>
/// Linter for evaluating cross-harness role parity, instruction drift, and routing readiness.
/// </summary>
public static class AgentSyncLinter
{
    public const string RuleUnsatisfiedRole = "KW-AGENT-SYNC-001";
    private const string RuleInstructionDrift = "KW-AGENT-SYNC-002";
    public const string RuleLowRoutingScore = "KW-AGENT-LINT-001";
    public const string RuleMissingTriggerPhrasing = "KW-AGENT-LINT-002";

    public static DiagnosticReport LintSet(AgentSet agentSet, string rootDirectoryPath) =>
        LintSet(agentSet, rootDirectoryPath, HarnessProfileConfig.ProductDefaults);

    public static DiagnosticReport LintSet(
        AgentSet agentSet,
        string rootDirectoryPath,
        HarnessProfileConfig harnessConfig)
    {
        ArgumentNullException.ThrowIfNull(agentSet);
        ArgumentNullException.ThrowIfNull(harnessConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectoryPath);

        DiagnosticReport report = new DiagnosticReport();
        IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> matrix = agentSet.GetRoleHarnessMatrix();
        IEnumerable<string> allRoles = agentSet.GetAllRoleNames();
        IReadOnlyDictionary<HarnessKind, HarnessCapabilityProfile> profiles = harnessConfig.Profiles;

        // 1. Cross-Harness Role Parity Check with Role Satisfaction Engine
        foreach (string role in allRoles)
        {
            matrix.TryGetValue(role, out Dictionary<HarnessKind, AgentModel>? harnessMap);
            harnessMap ??= new Dictionary<HarnessKind, AgentModel>();

            foreach ((HarnessKind harnessKind, HarnessCapabilityProfile? profile) in profiles)
            {
                bool satisfied = harnessMap.ContainsKey(harnessKind);

                // If not satisfied natively in the harness folder, check if it's satisfied via skill mapping or skill directory
                if (!satisfied)
                {
                    if (profile.MappedRoleSkillOverrides.TryGetValue(role, out string? skillName))
                    {
                        string skillDir = Path.Combine(rootDirectoryPath, ".agents", "skills", skillName);
                        // Require the canonical SKILL.md inside the mapped skill folder —
                        // a root-level SKILL.md must not falsely satisfy the role.
                        if (File.Exists(Path.Combine(skillDir, "SKILL.md")))
                        {
                            satisfied = true; // Role is satisfied via mapped skill
                        }
                    }
                }

                if (!satisfied)
                {
                    report.Add(new Diagnostic(RuleUnsatisfiedRole, Severity.Warning,
                        $"Agent role '{role}' is missing in harness '{harnessKind}' ({profile.DirectoryName}). All 6 harness configurations should remain synchronized.",
                        role, profile.DirectoryName));
                }
            }

            // 2. Instruction Drift Detection across existing harness implementations of this role
            List<AgentModel> roleAgents = harnessMap.Values.ToList();
            if (roleAgents.Count >= 2)
            {
                AgentModel baseAgent = roleAgents[0];
                Dictionary<string, double> baseVec = TextVectorizer.Vectorize(baseAgent.InstructionsBody);

                for (int i = 1; i < roleAgents.Count; i++)
                {
                    AgentModel compareAgent = roleAgents[i];
                    Dictionary<string, double> compareVec = TextVectorizer.Vectorize(compareAgent.InstructionsBody);
                    double similarity = TextVectorizer.CosineSimilarity(baseVec, compareVec);

                    if (similarity < 0.70)
                    {
                        report.Add(new Diagnostic(RuleInstructionDrift, Severity.Warning,
                            $"Instruction drift detected for role '{role}' between '{baseAgent.Harness}' and '{compareAgent.Harness}' (similarity: {similarity:P0}).",
                            role, compareAgent.FilePath));
                    }
                }
            }

            // 3. Routing Description Score and Trigger Quality for each agent instance
            foreach (AgentModel agent in roleAgents)
            {
                if (!string.IsNullOrWhiteSpace(agent.Description))
                {
                    string desc = agent.Description.Trim();
                    if (!RoutingLinter.TriggerClauseRegex().IsMatch(desc))
                    {
                        report.Add(new Diagnostic(RuleMissingTriggerPhrasing, Severity.Warning,
                            $"Agent '{agent.RoleName}' in '{agent.Harness}' description is an action summary or lacks trigger phrasing. Frame when the orchestrator should invoke this agent (e.g. 'Use when...', 'Invoke when...', 'Trigger when...').",
                            agent.RoleName, agent.FilePath,
                            "Add an explicit 'Use when...' or 'Invoke when...' trigger clause to clarify activation intent."));
                    }

                    Skill dummySkill = new Skill
                    {
                        SkillFilePath = agent.FilePath,
                        DirectoryPath = agent.DirectoryPath,
                        RawFrontmatter = string.Empty,
                        InstructionsBody = agent.InstructionsBody,
                        Frontmatter = new SkillFrontmatter
                        {
                            Name = agent.RoleName,
                            Description = agent.Description
                        }
                    };

                    DescriptionScore score = DescriptionScorer.Score(dummySkill);
                    if (score.Total < 50)
                    {
                        report.Add(new Diagnostic(RuleLowRoutingScore, Severity.Info,
                            $"Agent '{agent.RoleName}' in '{agent.Harness}' has low description routing score ({score.Total}/100). Consider adding explicit 'Use when...' and 'Do NOT use for...' clauses.",
                            agent.RoleName, agent.FilePath));
                    }
                }
            }
        }

        return report;
    }
}
