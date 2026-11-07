using System.Text;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the full-suite gate key, the council flag, and the CI template call in the review configuration.
/// </summary>
/// <remarks>
/// <para>
/// A gate with <c>full-suite: true</c> is one that runs the whole product suite, so <c>kyber-weave review gates</c>
/// refuses it unless the caller passes <c>--council</c> (or the terminal is interactive). The key is set on exactly
/// <c>test</c>, <c>ts-test</c> and <c>cargo-test</c>. The <c>test</c> and <c>ts-test</c> argv pass <c>--council</c>
/// to <c>scripts/proc-guard.sh run</c>, which needs it to admit the full run. The CI template passes <c>--council</c>
/// to <c>review gates</c>, because CI is the other caller allowed to run the full suite.
/// </para>
/// <para>
/// This suite reads the YAML and the template only. It starts no process.
/// </para>
/// </remarks>
public sealed class ReviewGateFullSuiteConfigTests
{
    private const string CouncilFlagText = "--council";

    private static readonly string[] FullSuiteGates = ["test", "ts-test", "cargo-test"];

    /// <summary>The three gates that run the whole product suite each carry the <c>full-suite</c> key set to true.</summary>
    /// <remarks>
    /// Without the key the refusal does not apply, and a dev agent could start the full suite through the review gate.
    /// </remarks>
    [Theory]
    [InlineData("test")]
    [InlineData("ts-test")]
    [InlineData("cargo-test")]
    public void FullSuiteGateCarriesTheFullSuiteKeySetToTrue(string gateId)
    {
        Assert.Equal("true", GateScalar(gateId, "full-suite"));
    }

    /// <summary>
    /// No gate other than <c>test</c>, <c>ts-test</c> and <c>cargo-test</c> carries <c>full-suite: true</c>.
    /// </summary>
    /// <remarks>
    /// A targeted gate marked full-suite would be refused by the review even though it runs one filtered check, and a
    /// missing key on a full gate would let it run unguarded. Both are caught by comparing the whole set.
    /// </remarks>
    [Fact]
    public void OnlyTheThreeFullSuiteGatesCarryTheKeySetToTrue()
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
    /// The <c>test</c> and <c>ts-test</c> gates pass <c>--council</c> after <c>run</c> and before the <c>--</c> separator,
    /// so the watchdog consumes it and admits the full run.
    /// </summary>
    /// <remarks>
    /// Placed after <c>--</c> the flag would reach the test runner, which does not know it. Placed before <c>run</c> the
    /// watchdog would not see it as a flag at all.
    /// </remarks>
    [Theory]
    [InlineData("test")]
    [InlineData("ts-test")]
    public void FullSuiteProcGuardArgvCarriesCouncilAfterRunAndBeforeTheSeparator(string gateId)
    {
        string[] run = GateRunArgv(gateId);

        Assert.Equal(["scripts/proc-guard.sh", "run"], run.Take(2));
        Assert.Equal(1, run.Count(word => word == CouncilFlagText));
        int councilIndex = Array.IndexOf(run, CouncilFlagText);
        int separator = Array.IndexOf(run, "--");
        Assert.True(separator > 2, "The gate's proc-guard flags must end with '--' before the wrapped command.");
        Assert.True(
            councilIndex > 1 && councilIndex < separator,
            $"--council must sit between 'run' and '--' in gate '{gateId}': {string.Join(' ', run)}");
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
        string[] lines = File.ReadAllLines(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "templates",
            "github-actions",
            "kyber-weave-review-gate.yml"));

        int start = Array.FindIndex(lines, line => line.Contains("kyber-weave review gates .", StringComparison.Ordinal));
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

        Assert.Contains(CouncilFlagText, command.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
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

            YamlSequenceNode argv = (YamlSequenceNode)Child(gate, "run")!;
            return argv.Children.Select(item => ((YamlScalarNode)item).Value ?? string.Empty).ToArray();
        }

        throw new InvalidOperationException(
            $"review.gates has no gate with id '{gateId}' in .kyber-weave/kyber-weave.yml.");
    }

    private static YamlMappingNode LoadRoot()
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".kyber-weave", "kyber-weave.yml");
        using StreamReader reader = new(path);
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
