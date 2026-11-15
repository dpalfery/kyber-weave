using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins which review gates in <c>.kyber-weave/kyber-weave.yml</c> run through the process watchdog and which run
/// <c>dotnet test</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// Only the dash <c>ts-test</c> gate starts with <c>scripts/proc-guard.sh run --</c>, so Node/Vitest worker caps and
/// leftover checks apply when the review runs it. The <c>.NET</c> test gates (<c>test</c> and <c>test-policy</c>) run
/// <c>dotnet test</c> directly without <c>scripts/proc-guard.sh</c> so Roslyn/MSBuild build servers stay warm and
/// watchdog teardown overhead does not slow <c>.NET</c> runs.
/// </para>
/// <para>
/// This suite reads the YAML only. It starts no process.
/// </para>
/// </remarks>
public sealed class ReviewGateProcGuardWiringTests
{
    /// <summary>
    /// The watchdog prefix of a full-suite gate. <c>--council</c> sits between <c>run</c> and the <c>--</c> separator, so
    /// the watchdog consumes it and admits the full run; after <c>--</c> it would reach the wrapped command instead.
    /// </summary>
    private static readonly string[] CouncilWrapper = ["scripts/proc-guard.sh", "run", "--council", "--"];

    /// <summary>
    /// The <c>ts-test</c> review gate starts with the watchdog wrapper and then runs the dash test command unchanged.
    /// </summary>
    /// <remarks>
    /// Without the wrapper, a vitest fork-bomb inside the review would run with no cap and no leftover check. Keeping the
    /// wrapped command intact means the wrapper adds a guard without changing what the gate tests.
    /// </remarks>
    [Fact]
    public void TsTestGateArgvStartsWithProcGuardRunAndKeepsDashTestCommand()
    {
        string[] run = GateRunArgv("ts-test");

        Assert.Equal(CouncilWrapper, run.Take(4));
        Assert.Equal(["npm", "run", "--prefix", "dash", "test", "--", "--coverage"], run.Skip(4).Take(7));
    }

    /// <summary>
    /// The <c>test</c> and <c>test-policy</c> review gates run <c>dotnet test</c> directly without
    /// <c>scripts/proc-guard.sh</c>.
    /// </summary>
    /// <remarks>
    /// Only KyberDash/Node suites require the process watchdog wrapper. Wrapping <c>.NET</c> test gates shuts down
    /// warm Roslyn/MSBuild build servers on every invocation and adds watchdog teardown latency.
    /// </remarks>
    [Theory]
    [InlineData("test", "tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("test-policy", "tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj")]
    public void DotnetTestGateArgvRunsDotnetTestDirectlyWithoutProcGuard(string gateId, string projectPath)
    {
        string[] run = GateRunArgv(gateId);

        Assert.All(run, word => Assert.NotEqual("scripts/proc-guard.sh", word));
        Assert.Equal(["dotnet", "test", projectPath, "-c", "Release", "--no-build"], run.Take(6));
    }

    /// <summary>
    /// No review gate other than <c>ts-test</c> wraps its command in <c>scripts/proc-guard.sh</c>.
    /// </summary>
    /// <remarks>
    /// Keeps the process watchdog scoped strictly to KyberDash JavaScript runs while <c>.NET</c> and Rust gates execute
    /// their toolchains directly.
    /// </remarks>
    [Fact]
    public void OnlyTheTsTestGateRunsThroughProcGuard()
    {
        List<string> guarded = [];
        foreach (YamlNode node in ((YamlSequenceNode)Child((YamlMappingNode)Child(LoadRoot(), "review")!, "gates")!).Children)
        {
            YamlMappingNode gate = (YamlMappingNode)node;
            if (Child(gate, "run") is YamlSequenceNode argv &&
                argv.Children.Any(item => ((YamlScalarNode)item).Value == "scripts/proc-guard.sh"))
            {
                guarded.Add(Scalar(gate, "id") ?? "(no id)");
            }
        }

        Assert.Equal(["ts-test"], guarded);
    }

    private static string[] GateRunArgv(string gateId)
    {
        YamlMappingNode root = LoadRoot();
        YamlMappingNode review = (YamlMappingNode)Child(root, "review")!;
        YamlSequenceNode gates = (YamlSequenceNode)Child(review, "gates")!;

        foreach (YamlNode node in gates.Children)
        {
            YamlMappingNode gate = (YamlMappingNode)node;
            if (Scalar(gate, "id") != gateId)
                continue;

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
                return pair.Value;
        }

        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        return (Child(map, key) as YamlScalarNode)?.Value;
    }
}
