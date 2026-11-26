using KyberWeave.Tests.Corpus;
using Xunit;
using KyberWeave.Application.Arbiter;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the state-path literals to their single sources: <c>ArbiterStateDirectory</c> for the project store and
/// <c>UserStatePaths</c> for the user-level layout.
/// </summary>
public sealed class StatePathLiteralTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly HashSet<string> SingleSources = new(StringComparer.Ordinal)
    {
        "src/KyberWeave.Kernel/Arbiter/ArbiterStateDirectory.cs",
        "src/KyberWeave.Kernel/FileSystem/UserStatePaths.cs"
    };

    /// <summary>
    /// Files whose <c>.kyber-weave/</c> text is message prose, generated prose, or the JavaScript shim,
    /// not a path this code uses to reach a file.
    /// </summary>
    private static readonly HashSet<string> ProseAllowList = new(StringComparer.Ordinal)
    {
        "src/KyberWeave.Application/Arbiter/ArbiterConfigResolver.cs",
        "src/KyberWeave.Application/Arbiter/Hooks/ArbiterHostConfig.cs",
        "src/KyberWeave.Application/Docs/Scaffolding/ConfigRegMarkdown.cs",
        "src/KyberWeave.Application/Docs/Scaffolding/DocsScaffoldTemplates.cs",
        "src/KyberWeave.Application/Docs/Validation/ConfigRegValidator.cs",
        "src/KyberWeave.Application/Docs/Validation/DocSpecValidator.cs",
        "src/KyberWeave.Application/Docs/Validation/KwSkillRulesValidator.cs",
        "src/KyberWeave.Application/Doctor/McpServerLiveProbe.cs",
        "src/KyberWeave.Application/Doctor/PostCheckoutHookText.cs",
        "src/KyberWeave.Application/Doctor/WorktreeIndexEnsurer.cs",
        "src/KyberWeave.Application/Hooks/HooksConfigMerger.cs",
        "src/KyberWeave.Application/Hooks/YamlHooksConfigSource.cs",
        "src/KyberWeave.Application/RunStart/RunStartService.cs",
        "src/KyberWeave.Application/Squad/Rendering/ArbiterPluginShim.cs",
        "src/KyberWeave.Cli/CliApplication.cs",
        "src/KyberWeave.Cli/Commands/Agents/AgentCatalogSettings.cs",
        "src/KyberWeave.Cli/Commands/AnalysisSettings.cs",
        "src/KyberWeave.Cli/Commands/Docs/DocsCommandComposition.cs",
        "src/KyberWeave.Cli/Commands/Docs/DocsInitSettings.cs",
        "src/KyberWeave.Cli/Commands/Docs/DocsMcpWireCommand.cs",
        "src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs",
        "src/KyberWeave.Cli/Commands/Review/ReviewMigrateTaskGatesCommand.cs",
        "src/KyberWeave.Data/Docs/Analysis/Persistence/SqliteAnalysisPersistence.cs",
        "src/KyberWeave.Mcp/DocsServerInstructions.cs"
    };

    [Fact]
    public void StatePathLiteralsLiveInTheirSingleSource()
    {
        List<string> offenders = [];

        foreach (string path in Corpus.EnumerateFiles("src", "*.cs"))
        {
            if (SingleSources.Contains(path))
            {
                continue;
            }

            string text = Corpus.ReadText(path);
            if (text.Contains("\"artifacts\", \"arbiter\"", StringComparison.Ordinal)
                || text.Contains("\"artifacts\",\"arbiter\"", StringComparison.Ordinal)
                || text.Contains("\".kyber-weave\"", StringComparison.Ordinal))
            {
                offenders.Add(path);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void StatePathPrefixesLiveInUserStatePaths()
    {
        List<string> offenders = [];

        foreach (string path in Corpus.EnumerateFiles("src", "*.cs"))
        {
            if (SingleSources.Contains(path) || ProseAllowList.Contains(path))
            {
                continue;
            }

            foreach (string line in Corpus.ReadText(path).Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (line.Contains(".kyber-weave/", StringComparison.Ordinal))
                {
                    offenders.Add(path);
                    break;
                }
            }
        }

        Assert.Empty(offenders);
    }
}
