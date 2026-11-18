using System.Text;
using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the full-suite gate key, the <c>requires-paths</c> prerequisite key, the council flag, and the CI template
/// call in the review configuration.
/// </summary>
/// <remarks>
/// <para>
/// A gate with <c>full-suite: true</c> is one that runs the whole product suite, so <c>kyber-weave review gates</c>
/// refuses it unless the caller passes <c>--council</c> (or the terminal is interactive). The key is set on exactly
/// <c>test</c>, <c>integration-test</c>, <c>ts-test</c> and <c>cargo-test</c>. Every one of them declares its command
/// directly — <c>test</c> and <c>integration-test</c> run <c>dotnet test</c> over their own project and <c>ts-test</c>
/// runs <c>npm run --prefix dash test</c>, with no process wrapper in between — so
/// the gate-level <c>full-suite: true</c> key is the whole refusal. The CI template passes <c>--council</c> to
/// <c>review gates</c>, because CI is the other caller allowed to run the full suite.
/// </para>
/// <para>
/// A gate that runs an npm script under <c>dash/</c> needs <c>dash/node_modules</c> first, and says so with
/// <c>requires-paths</c>. Without it a contributor on a fresh clone is handed a wall of module-not-found output
/// instead of the one sentence that fixes it.
/// </para>
/// <para>
/// This suite reads the YAML and the template only. It starts no process.
/// </para>
/// </remarks>
public sealed class ReviewGateFullSuiteConfigTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string CouncilFlagText = "--council";

    private static readonly string[] FullSuiteGates = ["test", "integration-test", "ts-test", "cargo-test"];

    /// <summary>Every gate that runs a whole product suite carries the <c>full-suite</c> key set to true.</summary>
    /// <remarks>
    /// Without the key the refusal does not apply, and a dev agent could start the full suite through the review gate.
    /// </remarks>
    [Theory]
    [InlineData("test")]
    [InlineData("integration-test")]
    [InlineData("ts-test")]
    [InlineData("cargo-test")]
    public void FullSuiteGateCarriesTheFullSuiteKeySetToTrue(string gateId)
    {
        Assert.Equal("true", GateScalar(gateId, "full-suite"));
    }

    /// <summary>
    /// No gate other than <c>test</c>, <c>integration-test</c>, <c>ts-test</c> and <c>cargo-test</c> carries
    /// <c>full-suite: true</c>.
    /// </summary>
    /// <remarks>
    /// A targeted gate marked full-suite would be refused by the review even though it runs one filtered check, and a
    /// missing key on a full gate would let it run unguarded. Both are caught by comparing the whole set.
    /// </remarks>
    [Fact]
    public void OnlyTheWholeSuiteGatesCarryTheKeySetToTrue()
    {
        List<string> keyed = [];
        foreach (YamlMappingNode gate in ReviewGates())
        {
            if (Scalar(gate, "full-suite") == "true")
            {
                keyed.Add(Scalar(gate, "id") ?? "(no id)");
            }
        }

        Assert.Equal(FullSuiteGates.Order(StringComparer.Ordinal), keyed.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The CI template's <c>kyber-weave review gates</c> call passes <c>--council</c>.
    /// </summary>
    /// <remarks>
    /// The CI job is a caller allowed to run the full suite. Without the flag the gates it runs would be refused, and CI
    /// would fail on every change that needs a full review.
    /// </remarks>
    [Fact]
    public void CiTemplateReviewGatesCallPassesCouncil()
    {
        string[] lines = Corpus.ReadText("templates/github-actions/kyber-weave-review-gate.yml")
            .ReplaceLineEndings("\n")
            .Split('\n');

        int start = Array.FindIndex(lines,
            line => line.Contains("kyber-weave review gates .", StringComparison.Ordinal));
        Assert.True(start >= 0, "The CI template has no 'kyber-weave review gates .' call.");

        // The call continues over lines that end in a backslash; join them into one command.
        StringBuilder command = new();
        for (int index = start; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            command.Append(line.TrimEnd('\\')).Append(' ');
            if (!line.EndsWith('\\'))
            {
                break;
            }
        }

        Assert.Contains(CouncilFlagText, command.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Every gate that runs an npm script under <c>dash/</c> declares <c>dash/node_modules</c> in
    /// <c>requires-paths</c>.
    /// </summary>
    /// <remarks>
    /// The dash gates are the ones that need an install first. A gate that declares no prerequisite runs anyway and
    /// fails on the missing directory, which reads as a defect in the change under review instead of the missing
    /// install it is. Selecting the gates by what they run rather than by id keeps a renamed or newly added dash
    /// gate inside the rule.
    /// </remarks>
    [Fact]
    public void EveryGateThatRunsAnNpmScriptUnderDashDeclaresTheNodeModulesPrerequisite()
    {
        const string nodeModules = "dash/node_modules";
        string[] offenders = ReviewGates()
            .Where(gate => RunsDashNpm(GateRunArgv(gate)))
            .Where(gate => !RequiredPaths(gate).Contains(nodeModules, StringComparer.Ordinal))
            .Select(gate => $"review.gates '{Scalar(gate, "id") ?? "(no id)"}' runs npm under dash/ " +
                            $"but does not declare requires-paths: [{nodeModules}].")
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool RunsDashNpm(string[] run)
    {
        return run.Contains("npm", StringComparer.Ordinal)
               && run.Contains("--prefix", StringComparer.Ordinal)
               && run.Contains("dash", StringComparer.Ordinal);
    }

    private static string[] RequiredPaths(YamlMappingNode gate)
    {
        return Child(gate, "requires-paths") is YamlSequenceNode paths
            ? [.. paths.Children.Select(item => ((YamlScalarNode)item).Value ?? string.Empty)]
            : [];
    }

    private static IEnumerable<YamlMappingNode> ReviewGates()
    {
        YamlMappingNode review = (YamlMappingNode)Child(LoadRoot(), "review")!;
        foreach (YamlNode node in ((YamlSequenceNode)Child(review, "gates")!).Children)
        {
            yield return (YamlMappingNode)node;
        }
    }

    private static string? GateScalar(string gateId, string key)
    {
        foreach (YamlMappingNode gate in ReviewGates())
        {
            if (Scalar(gate, "id") == gateId)
            {
                return Scalar(gate, key);
            }
        }

        return null;
    }

    private static string[] GateRunArgv(string gateId)
    {
        foreach (YamlMappingNode gate in ReviewGates())
        {
            if (Scalar(gate, "id") != gateId)
            {
                continue;
            }

            return GateRunArgv(gate);
        }

        throw new InvalidOperationException(
            $"review.gates has no gate with id '{gateId}' in .kyber-weave/kyber-weave.yml.");
    }

    private static string[] GateRunArgv(YamlMappingNode gate)
    {
        YamlSequenceNode argv = (YamlSequenceNode)Child(gate, "run")!;
        return argv.Children.Select(item => ((YamlScalarNode)item).Value ?? string.Empty).ToArray();
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
