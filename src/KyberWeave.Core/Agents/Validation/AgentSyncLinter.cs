using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Text;
using KyberWeave.Core.Skills.Validation;

namespace KyberWeave.Core.Agents.Validation;

/// <summary>
/// Linter for evaluating cross-harness role parity, instruction drift, and routing readiness.
/// </summary>
public static class AgentSyncLinter
{
    public const string RuleUnsatisfiedRole = "KW-AGENT-SYNC-001";
    public const string RuleInstructionDrift = "KW-AGENT-SYNC-002";
    public const string RuleLowRoutingScore = "KW-AGENT-LINT-001";

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

        var report = new DiagnosticReport();
        var matrix = agentSet.GetRoleHarnessMatrix();
        var allRoles = agentSet.GetAllRoleNames();
        var profiles = harnessConfig.Profiles;

        // 1. Cross-Harness Role Parity Check with Role Satisfaction Engine
        foreach (var role in allRoles)
        {
            matrix.TryGetValue(role, out var harnessMap);
            harnessMap ??= new Dictionary<HarnessKind, AgentModel>();

            foreach (var (harnessKind, profile) in profiles)
            {
                bool satisfied = harnessMap.ContainsKey(harnessKind);

                // If not satisfied natively in the harness folder, check if it's satisfied via skill mapping or skill directory
                if (!satisfied)
                {
                    if (profile.MappedRoleSkillOverrides.TryGetValue(role, out var skillName))
                    {
                        var skillDir = Path.Combine(rootDirectoryPath, ".agents", "skills", skillName);
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
            var roleAgents = harnessMap.Values.ToList();
            if (roleAgents.Count >= 2)
            {
                var baseAgent = roleAgents[0];
                var baseVec = TextVectorizer.Vectorize(baseAgent.InstructionsBody);

                for (int i = 1; i < roleAgents.Count; i++)
                {
                    var compareAgent = roleAgents[i];
                    var compareVec = TextVectorizer.Vectorize(compareAgent.InstructionsBody);
                    var similarity = TextVectorizer.CosineSimilarity(baseVec, compareVec);

                    if (similarity < 0.70)
                    {
                        report.Add(new Diagnostic(RuleInstructionDrift, Severity.Warning,
                            $"Instruction drift detected for role '{role}' between '{baseAgent.Harness}' and '{compareAgent.Harness}' (similarity: {similarity:P0}).",
                            role, compareAgent.FilePath));
                    }
                }
            }

            // 3. Routing Description Score for each agent instance
            foreach (var agent in roleAgents)
            {
                if (!string.IsNullOrWhiteSpace(agent.Description))
                {
                    var dummySkill = new Skill
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

                    var score = DescriptionScorer.Score(dummySkill);
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
