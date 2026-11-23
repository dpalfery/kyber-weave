using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <c>requires-paths</c> prerequisites of the TypeScript review gates and the repository's ignore line for
/// agent harness worktrees.
/// </summary>
/// <remarks>
/// <para>
/// The TypeScript gates that read <c>dash/web</c> resolve its <c>src</c> imports from <c>dash/web/node_modules</c>, so
/// <c>ts-typecheck</c>, <c>ts-test</c> and <c>ts-lint</c> declare that directory next to <c>dash/node_modules</c>. Without
/// it a fresh clone is handed module-not-found output instead of the one install that fixes it. <c>ts-reachable</c>
/// follows file references only and needs <c>dash/node_modules</c> alone.
/// </para>
/// <para>
/// The ignore file must list <c>.agent_worktrees/</c>, so a worker's nested checkout never shows up as source.
/// </para>
/// <para>
/// This suite reads the YAML and the ignore file only. It starts no process.
/// </para>
/// </remarks>
public sealed class ReviewGateRequiredPathsConfigTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string DashNodeModules = "dash/node_modules";
    private const string DashWebNodeModules = "dash/web/node_modules";
    private const string AgentWorktreesIgnoreLine = ".agent_worktrees/";

    /// <summary>
    /// <c>ts-typecheck</c>, <c>ts-test</c> and <c>ts-lint</c> each require both <c>dash/node_modules</c> and
    /// <c>dash/web/node_modules</c>.
    /// </summary>
    /// <remarks>
    /// Each of the three resolves <c>web/src</c> imports from <c>dash/web/node_modules</c>. Without the second path a
    /// fresh clone reaches the tool and fails on a missing web dependency instead of the install that fixes it.
    /// </remarks>
    [Theory]
    [InlineData("ts-typecheck")]
    [InlineData("ts-test")]
    [InlineData("ts-lint")]
    public void WebTypeScriptGatesRequireBothNodeModulesDirectories(string gateId)
    {
        string[] paths = RequiredPaths(GateNamed(gateId));

        Assert.Contains(DashNodeModules, paths, StringComparer.Ordinal);
        Assert.Contains(DashWebNodeModules, paths, StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>ts-reachable</c> requires <c>dash/node_modules</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>check:reachable</c> follows file references only and never resolves a web import, so the extra directory would
    /// refuse the gate for nothing. The list stays minimal, so the comparison is exact.
    /// </remarks>
    [Fact]
    public void ReachableGateRequiresDashNodeModulesOnly()
    {
        Assert.Equal([DashNodeModules], RequiredPaths(GateNamed("ts-reachable")));
    }

    /// <summary>The root ignore file holds the <c>.agent_worktrees/</c> line.</summary>
    /// <remarks>
    /// Harness worktrees created inside the repository are nested checkouts. Without the line they show up as untracked
    /// source in every status and every commit sweep.
    /// </remarks>
    [Fact]
    public void GitignoreIgnoresAgentWorktreesDirectory()
    {
        string[] lines = Corpus.ReadText(".gitignore")
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();

        Assert.Contains(AgentWorktreesIgnoreLine, lines, StringComparer.Ordinal);
    }

    private static YamlMappingNode GateNamed(string gateId)
    {
        YamlMappingNode review = (YamlMappingNode)Child(LoadRoot(), "review")!;
        foreach (YamlNode node in ((YamlSequenceNode)Child(review, "gates")!).Children)
        {
            YamlMappingNode gate = (YamlMappingNode)node;
            if (Scalar(gate, "id") == gateId)
            {
                return gate;
            }
        }

        throw new InvalidOperationException(
            $"review.gates has no gate with id '{gateId}' in .kyber-weave/kyber-weave.yml.");
    }

    private static string[] RequiredPaths(YamlMappingNode gate)
    {
        return Child(gate, "requires-paths") is YamlSequenceNode paths
            ? [.. paths.Children.Select(item => ((YamlScalarNode)item).Value ?? string.Empty)]
            : [];
    }

    private static YamlMappingNode LoadRoot()
    {
        using StringReader reader = new(Corpus.ReadText(".kyber-weave/kyber-weave.yml"));
        YamlStream stream = new();
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        return Child(map, key) is YamlScalarNode scalar ? scalar.Value : null;
    }
}
