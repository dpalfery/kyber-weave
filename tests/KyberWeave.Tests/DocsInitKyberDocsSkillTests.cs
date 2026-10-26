using KyberWeave.Cli.Commands.Docs;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T14 RED: <c>docs init</c> deploys the renamed Kyber-Docs authoring skill and warns
/// about a leftover retired skill directory the install leaves behind. Both facts fail
/// on the phase-1 tree until T15-T17 lands.
/// </summary>
public sealed class DocsInitKyberDocsSkillTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The skill <c>docs init</c> deploys and points at is the renamed
    /// <c>kyber-docs-authoring</c>, not the retired <c>kyber-weave-docs</c> identity.
    /// </summary>
    [Fact]
    public void ExecuteNamesKyberDocsAuthoringSkill()
    {
        CommandApp app = new CommandApp();
        app.Configure(config =>
        {
            config.AddBranch("docs", docs =>
            {
                docs.AddCommand<DocsInitCommand>("init");
            });
        });

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture
            .Run(() => app.Run(["docs", "init", _temp.Path, "--no-skill"]));

        Assert.Equal(0, execution.Result);
        Assert.Contains("kyber-docs-authoring", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave-docs", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retired <c>kyber-weave-docs</c> directory left under the target's skills root
    /// warns with its path and the exact cleanup command. T13a observed APM's pruning
    /// as lockfile-gated on both 0.28.0 and 0.35.0: without a lockfile both versions
    /// leave the stale directory behind, so the warning fires for installs either
    /// version performed. No 0.34.0 behaviour is pinned: it was never observed.
    /// </summary>
    [Theory]
    [InlineData("0.28.0")]
    [InlineData("0.35.0")]
    public void ExecuteWithStaleRetiredSkillDirAndNoLockfileWarnsWithCleanupCommand(string apmVersion)
    {
        string repoRoot = Path.Combine(_temp.Path, $"stale-{apmVersion}");
        string skillsRoot = Path.Combine(repoRoot, ".agents", "skills");
        string staleDir = Path.Combine(skillsRoot, "kyber-weave-docs");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(
            Path.Combine(staleDir, "SKILL.md"), "---\nname: kyber-weave-docs\n---\nStale.\n");
        Assert.Empty(Directory.EnumerateFiles(skillsRoot, "*.lock", SearchOption.AllDirectories));

        CommandApp app = new CommandApp();
        app.Configure(config =>
        {
            config.AddBranch("docs", docs =>
            {
                docs.AddCommand<DocsInitCommand>("init");
            });
        });

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture
            .Run(() => app.Run(["docs", "init", repoRoot, "--no-skill"]));

        string normalizedOutput = Normalize(execution.Output);

        Assert.Equal(0, execution.Result);
        Assert.Contains("warn", normalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kyber-weave-docs", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains(Normalize(staleDir), normalizedOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"rm -rf \"{Normalize(staleDir)}\"", normalizedOutput, StringComparison.Ordinal);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
