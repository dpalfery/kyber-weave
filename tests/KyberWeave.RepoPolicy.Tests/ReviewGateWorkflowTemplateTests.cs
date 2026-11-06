using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the copy-ready review-gate workflow in <c>templates/github-actions</c>. The host copies this
/// file into its own repository; no Kyber-Weave command writes <c>.github/workflows/</c>. A template
/// that drifts from these rules ships a workflow that runs unpinned actions, widens its token, skips
/// full history so the base range is wrong, or interpolates event data into a shell.
/// </summary>
/// <remarks>
/// The template is read at runtime from the repository root, so the suite compiles before the
/// template exists and reports its absence as the failure.
/// </remarks>
public sealed class ReviewGateWorkflowTemplateTests
{
    private static readonly string TemplatePath =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "templates", "github-actions", "kyber-weave-review-gate.yml");

    private static readonly string ReadmePath =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "templates", "github-actions", "README.md");

    private static readonly Regex FullCommitSha = new(@"@[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    private static readonly string[] ReadOnlyPermissions = ["contents: read"];

    /// <summary>The five toolchain families; one step each, sorted ordinally for comparison.</summary>
    private static readonly string[] ToolchainFamilies = ["dotnet", "go", "node", "python", "rust"];

    [Fact]
    public void TheReviewGateTemplateParsesAsASingleYamlMapping()
    {
        YamlMappingNode template = LoadTemplate();

        Assert.NotEmpty(template.Children);
    }

    [Fact]
    public void EveryActionIsPinnedToAFullCommitSha()
    {
        YamlMappingNode template = LoadTemplate();

        string[] uses = ValuesOfKey(template, "uses").Select(ScalarValue).ToArray();
        string[] unpinned = uses.Where(value => !FullCommitSha.IsMatch(value)).ToArray();

        Assert.NotEmpty(uses);
        Assert.True(
            unpinned.Length == 0,
            $"Every uses: must pin a full commit SHA; unpinned: {string.Join(", ", unpinned)}.");
    }

    [Fact]
    public void RuntimeVersionsArePinnedNotFloating()
    {
        YamlMappingNode template = LoadTemplate();

        string[] versions = ValuesOfKey(template, "node-version").Concat(ValuesOfKey(template, "python-version"))
            .Select(ScalarValue).ToArray();
        string[] floating = versions.Where(value => !Regex.IsMatch(value, @"^\d+(\.\d+)*$")).ToArray();

        Assert.NotEmpty(versions);
        Assert.True(
            floating.Length == 0,
            $"node-version and python-version must name a specific version; floating: {string.Join(", ", floating)}.");
    }

    [Fact]
    public void TopLevelAndEveryJobGrantOnlyContentsRead()
    {
        YamlMappingNode template = LoadTemplate();

        AssertContentsReadOnly(template, "The top level");
        foreach ((string name, YamlMappingNode job) in JobsOf(template))
        {
            AssertContentsReadOnly(job, $"Job '{name}'");
        }
    }

    [Fact]
    public void CheckoutFetchesFullHistory()
    {
        YamlMappingNode template = LoadTemplate();

        YamlMappingNode[] checkouts = StepsOf(template)
            .Where(step => ActionName(step) == "actions/checkout")
            .ToArray();

        Assert.NotEmpty(checkouts);
        foreach (YamlMappingNode checkout in checkouts)
        {
            YamlMappingNode with = Assert.IsType<YamlMappingNode>(Required(checkout, "with", "A checkout step"));
            Assert.Equal("0", ScalarValue(Required(with, "fetch-depth", "A checkout step's with block")));
        }
    }

    /// <summary>
    /// Without <c>--base</c> the gates compare against an arbitrary default, so a pull request can pass
    /// while its real changes go unchecked. The step must name the base it compares against.
    /// </summary>
    [Fact]
    public void AStepRunsReviewGatesWithAnExplicitBase()
    {
        YamlMappingNode template = LoadTemplate();

        string? gateRun = RunBlocks(template).FirstOrDefault(run =>
            run.Contains("kyber-weave review gates", StringComparison.Ordinal));

        Assert.NotNull(gateRun);
        Assert.Contains("--base", gateRun, StringComparison.Ordinal);
    }

    /// <summary>
    /// Event values such as the branch name are attacker-controlled. Interpolating <c>${{ github.* }}</c>
    /// into a <c>run:</c> block lets a crafted branch name inject shell. Such values must reach the
    /// shell only through <c>env:</c>.
    /// </summary>
    [Fact]
    public void NoRunBlockInterpolatesGitHubContextIntoShell()
    {
        YamlMappingNode template = LoadTemplate();

        string[] interpolating = RunBlocks(template)
            .Where(run => run.Contains("${{ github.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            interpolating.Length == 0,
            $"A run: block interpolates ${{{{ github. }}}} into shell; pass it through env: instead. Found: {string.Join(" | ", interpolating)}.");
    }

    [Fact]
    public void ToolchainStepsCoverEachVersionOneFamilyExactlyOnce()
    {
        YamlMappingNode template = LoadTemplate();

        string[] families = StepsOf(template)
            .Select(ToolchainFamilyOf)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ToolchainFamilies, families);
    }

    /// <summary>
    /// A toolchain that runs before the host has chosen it would install and build stacks the repository
    /// does not use, so every toolchain step stays <c>if: false</c> until the host flips it.
    /// </summary>
    [Fact]
    public void EveryToolchainStepIsDisabledUntilTheHostEnablesIt()
    {
        YamlMappingNode template = LoadTemplate();

        string[] enabled = StepsOf(template)
            .Where(step => ToolchainFamilyOf(step) is not null)
            .Where(step => OptionalScalar(step, "if") != "false")
            .Select(step => ToolchainFamilyOf(step) ?? string.Empty)
            .ToArray();

        Assert.True(
            enabled.Length == 0,
            $"Toolchain steps must be guarded by 'if: false'; not disabled: {string.Join(", ", enabled)}.");
    }

    [Fact]
    public void TheTemplateReadmeListsTheReviewGateWorkflow()
    {
        string readme = File.ReadAllText(ReadmePath);

        Assert.Contains("kyber-weave-review-gate.yml", readme, StringComparison.Ordinal);
    }

    private static YamlMappingNode LoadTemplate()
    {
        Assert.True(
            File.Exists(TemplatePath),
            $"The review-gate workflow template is missing at '{TemplatePath}'.");

        YamlStream stream = new();
        using StringReader reader = new(File.ReadAllText(TemplatePath));
        stream.Load(reader);

        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static void AssertContentsReadOnly(YamlMappingNode scope, string where)
    {
        YamlMappingNode permissions = Assert.IsType<YamlMappingNode>(Required(scope, "permissions", where));
        string[] granted = permissions.Children
            .Select(entry => $"{ScalarValue(entry.Key)}: {ScalarValue(entry.Value)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            granted.SequenceEqual(ReadOnlyPermissions, StringComparer.Ordinal),
            $"{where} must grant exactly 'contents: read'; found: {string.Join(", ", granted)}.");
    }

    private static (string Name, YamlMappingNode Job)[] JobsOf(YamlMappingNode template)
    {
        return Assert.IsType<YamlMappingNode>(Required(template, "jobs", "The template"))
            .Children
            .Select(entry => (Name: ScalarValue(entry.Key), Job: Assert.IsType<YamlMappingNode>(entry.Value)))
            .ToArray();
    }

    private static YamlMappingNode[] StepsOf(YamlMappingNode template)
    {
        return JobsOf(template)
            .SelectMany(job =>
                Assert.IsType<YamlSequenceNode>(Required(job.Job, "steps", $"Job '{job.Name}'")).Children)
            .Select(step => Assert.IsType<YamlMappingNode>(step))
            .ToArray();
    }

    private static IEnumerable<string> RunBlocks(YamlMappingNode template)
    {
        return ValuesOfKey(template, "run").Select(ScalarValue);
    }

    /// <summary>The action repository of a <c>uses:</c> reference, without its <c>@ref</c> suffix.</summary>
    private static string? ActionName(YamlMappingNode step)
    {
        return OptionalScalar(step, "uses")?.Split('@')[0];
    }

    /// <summary>Maps a toolchain action to its family, or null for a step that is not a toolchain.</summary>
    private static string? ToolchainFamilyOf(YamlMappingNode step)
    {
        string? action = ActionName(step);
        if (action is null)
        {
            return null;
        }

        return action switch
        {
            "actions/setup-dotnet" => "dotnet",
            "actions/setup-node" => "node",
            "actions/setup-go" => "go",
            "actions/setup-python" => "python",
            _ when action.Contains("rust", StringComparison.OrdinalIgnoreCase) => "rust",
            _ => null
        };
    }

    /// <summary>Every value stored under <paramref name="key"/> anywhere beneath <paramref name="node"/>.</summary>
    private static IEnumerable<YamlNode> ValuesOfKey(YamlNode node, string key)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
            {
                if (entry.Key is YamlScalarNode keyNode && keyNode.Value == key)
                {
                    yield return entry.Value;
                }

                foreach (YamlNode nested in ValuesOfKey(entry.Value, key))
                {
                    yield return nested;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode child in sequence.Children)
            {
                foreach (YamlNode nested in ValuesOfKey(child, key))
                {
                    yield return nested;
                }
            }
        }
    }

    private static YamlNode Required(YamlMappingNode node, string key, string where)
    {
        Assert.True(
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value),
            $"{where} has no '{key}' key.");
        return value!;
    }

    private static string? OptionalScalar(YamlMappingNode node, string key)
    {
        return node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value)
            ? ScalarValue(value)
            : null;
    }

    private static string ScalarValue(YamlNode node)
    {
        return Assert.IsType<YamlScalarNode>(node).Value ?? string.Empty;
    }
}
