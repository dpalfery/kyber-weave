using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins that the review gates which run test processes are wrapped by the process watchdog in
/// <c>.kyber-weave/kyber-weave.yml</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>dotnet test</c> gate and the dash <c>ts-test</c> gate are the two review gates that start a test runner.
/// Each must start with <c>scripts/proc-guard.sh run --</c>, so the cap and the leftover check apply when the review
/// runs them. The wrapped command after <c>--</c> must stay the command the gate always ran.
/// </para>
/// <para>
/// A gate whose argv drops the wrapper runs unguarded, and the review would pass a run that had no cap. This suite reads
/// the YAML only. It starts no process.
/// </para>
/// </remarks>
public sealed class ReviewGateProcGuardWiringTests
{
    private static readonly string[] Wrapper = ["scripts/proc-guard.sh", "run", "--"];

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

        Assert.Equal(Wrapper, run.Take(3));
        Assert.Equal(["npm", "run", "--prefix", "dash", "test"], run.Skip(3).Take(5));
    }

    /// <summary>
    /// The <c>test</c> review gate (the <c>dotnet test</c> gate) starts with the watchdog wrapper and then runs
    /// <c>dotnet test</c>.
    /// </summary>
    /// <remarks>
    /// The .NET test run is the process class that caused the original incident. The wrapper is the cap on it, and the
    /// review must not run it outside that cap.
    /// </remarks>
    [Fact]
    public void DotnetTestGateArgvStartsWithProcGuardRunAndKeepsDotnetTestCommand()
    {
        string[] run = GateRunArgv("test");

        Assert.Equal(Wrapper, run.Take(3));
        Assert.Equal(["dotnet", "test"], run.Skip(3).Take(2));
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
