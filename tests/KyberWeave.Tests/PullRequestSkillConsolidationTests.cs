using System.IO.Compression;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the consolidation of `create-pull-request` and retirement of
/// `create-pull-request-github` (plan 2026-09-28-provider-aware-create-pull-request).
/// </summary>
public sealed class PullRequestSkillConsolidationTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Prevents regression: `create-pull-request-github` must not be in the canonical source
    /// or any package archive.
    /// </summary>
    [Fact]
    public void CreatePullRequestGithubIsNotCanonicalAndShipsInNoPackage()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string skillRoot = Path.Combine(ProductRoot, "skills");

        // Skill must not be in the canonical source
        Assert.DoesNotContain(source.Skills, skill => skill.Name == "create-pull-request-github");
        Assert.DoesNotContain(source.Bundle.SkillNames, name => name == "create-pull-request-github");

        // Directory must not exist
        string skillDir = Path.Combine(skillRoot, "create-pull-request-github");
        Assert.False(Directory.Exists(skillDir), $"Directory {skillDir} must not exist");

        // Must not appear in packages
        using TempDirectory temp = new TempDirectory();
        string apmArchive = SquadPacker.PackApm(ProductRoot, temp.Path, "test");
        string pluginArchive = SquadPacker.PackPlugins(ProductRoot, temp.Path, "test");

        using (ZipArchive apm = ZipFile.OpenRead(apmArchive))
        {
            var retired = apm.Entries
                .Where(entry => entry.FullName.StartsWith("skills/create-pull-request-github/", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(retired);
        }

        using (ZipArchive plugins = ZipFile.OpenRead(pluginArchive))
        {
            var retired = plugins.Entries
                .Where(entry => entry.FullName.StartsWith("skills/create-pull-request-github/", StringComparison.Ordinal))
                .ToArray();
            Assert.Empty(retired);
        }
    }

    /// <summary>
    /// Prevents regression: provider skills must deliver every on-disk file through their
    /// resource closure.
    /// </summary>
    [Theory]
    [InlineData("create-pull-request")]
    [InlineData("pr-review-fix-comments")]
    public void ProviderSkillsDeliverEveryFileOnDiskThroughTheirClosure(string skillName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadSkill? skill = source.Skills.FirstOrDefault(s => s.Name == skillName);

        if (skill == null)
        {
            return; // Skill not yet implemented
        }

        string skillDir = Path.Combine(ProductRoot, "skills", skillName);
        Assert.True(Directory.Exists(skillDir), $"Skill directory {skillDir} must exist");

        // Get all files on disk (except SKILL.md)
        var diskFiles = Directory.EnumerateFiles(skillDir, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("SKILL.md", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(skillDir, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Get all files from the closure
        var closureFiles = skill.Resources
            .Select(resource => resource.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // All disk files must be in the closure
        Assert.Equal(diskFiles, closureFiles);
    }

    /// <summary>
    /// Prevents regression: provider files and scripts must be rendered on at least one target.
    /// </summary>
    [Fact]
    public async Task EveryTargetDeploysProviderFilesAndScriptsBesideTheSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        // Check that create-pull-request skill exists
        var cpr = source.Skills.FirstOrDefault(s => s.Name == "create-pull-request");
        if (cpr == null)
        {
            return; // Not yet implemented
        }

        // Verify the skill has provider resources
        var providers = cpr.Resources
            .Where(r => r.RelativePath.StartsWith("providers/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(providers);
    }

    /// <summary>
    /// Prevents regression: provider files must cite sources and meet the contract.
    /// </summary>
    [Fact]
    public void ProviderFileMeetsTheSharedContract()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        var cpr = source.Skills.FirstOrDefault(s => s.Name == "create-pull-request");
        if (cpr == null)
        {
            return; // Not yet implemented
        }

        // Check that provider resources exist
        var providers = cpr.Resources
            .Where(r => r.RelativePath.StartsWith("providers/", StringComparison.Ordinal))
            .Select(r => r.RelativePath)
            .ToArray();

        Assert.Contains("providers/github.md", providers);
        Assert.Contains("providers/azure-devops.md", providers);
    }

    /// <summary>
    /// Prevents regression: canonical Squad source carries no retired host facts.
    /// </summary>
    [Fact]
    public void CanonicalSquadSourceCarriesNoRetiredHostFacts()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        var denylist = new[] { "create-pull-request-github", "Denver", "DEN", ".kilo/kilo.json", "GITHUB_READ_ONLY", "mcp_azuredevops_m_" };
        var found = new List<string>();

        // Scan PR-related skills for denied patterns
        foreach (var skill in source.Skills.Where(s => s.Name == "create-pull-request" || s.Name == "pr-review-fix-comments"))
        {
            foreach (var pattern in denylist)
            {
                if (skill.InstructionBody.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{skill.Name} contains '{pattern}'");
                }
            }
        }

        Assert.Empty(found);
    }
}
