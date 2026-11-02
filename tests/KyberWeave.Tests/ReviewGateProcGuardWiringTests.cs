using System.Globalization;
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
    private const int DefaultGateTimeoutSeconds = 900;

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

        Assert.Equal(["scripts/proc-guard.sh", "run"], run.Take(2));
        int separator = Array.IndexOf(run, "--");
        Assert.True(separator >= 2, "The test gate's proc-guard flags must end with '--' before the wrapped command.");
        Assert.Equal(["dotnet", "test"], run.Skip(separator + 1).Take(2));
    }

    /// <summary>
    /// The <c>test</c> review gate takes the machine-wide <c>dotnet-test</c> slot, with a bounded queue wait.
    /// </summary>
    /// <remarks>
    /// Each conductor's final council runs one full <c>dotnet test</c>, and several councils on one machine used to overlap.
    /// A gate without <c>--slot</c> would keep overlapping them; a gate without <c>--slot-wait</c> would inherit the guard's
    /// default and could give up before a long queue drained.
    /// </remarks>
    [Fact]
    public void DotnetTestGateTakesTheMachineWideTestSlotWithABoundedWait()
    {
        string[] run = GateRunArgv("test");
        string[] guardFlags = run.Skip(2).TakeWhile(word => word != "--").ToArray();

        Assert.Equal("dotnet-test", FlagValue(guardFlags, "--slot"));
        Assert.True(
            int.TryParse(FlagValue(guardFlags, "--slot-wait"), out int slotWaitSeconds) && slotWaitSeconds > 0,
            "The test gate must pass a positive --slot-wait.");
    }

    /// <summary>
    /// The gate's own timeout is longer than its queue wait, so waiting for the slot cannot use up the budget for the run.
    /// </summary>
    /// <remarks>
    /// The review runner kills a gate at <c>timeout-seconds</c> (900 by default) from the moment it starts the gate, and it
    /// cannot tell a queued gate from a hung one. The queue wait and the 900 seconds the run itself had must both fit.
    /// </remarks>
    [Fact]
    public void DotnetTestGateTimeoutCoversTheSlotWaitPlusTheDefaultRunBudget()
    {
        string[] run = GateRunArgv("test");
        int slotWaitSeconds = int.Parse(FlagValue(run.Skip(2).TakeWhile(word => word != "--").ToArray(), "--slot-wait")!, CultureInfo.InvariantCulture);
        int timeoutSeconds = int.Parse(GateScalar("test", "timeout-seconds") ?? "900", CultureInfo.InvariantCulture);

        Assert.True(
            timeoutSeconds >= slotWaitSeconds + DefaultGateTimeoutSeconds,
            $"The test gate's timeout-seconds ({timeoutSeconds}) must be at least its --slot-wait ({slotWaitSeconds}) plus {DefaultGateTimeoutSeconds}.");
    }

    /// <summary>
    /// <c>fast-loop.sh --regenerate</c> queues for the <c>dotnet-test</c> slot as long as the <c>test</c> gate does.
    /// </summary>
    /// <remarks>
    /// Both wait on the same slot. The guard's default wait is shorter than the gate's, so a regenerate that did not say so
    /// would give up with exit 88 under exactly the contention the slot serialises, while a gate queued beside it kept waiting.
    /// </remarks>
    [Fact]
    public void FastLoopRegenerateWaitsForTheSlotAsLongAsTheTestGate()
    {
        string[] gateFlags = GateRunArgv("test").Skip(2).TakeWhile(word => word != "--").ToArray();
        string script = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "fast-loop.sh"));
        string regenerate = Assert.Single(
            script.Split('\n').Select(line => line.Trim()),
            line => line.StartsWith("scripts/proc-guard.sh run", StringComparison.Ordinal) && line.Contains("-xml", StringComparison.Ordinal));
        string[] regenerateFlags = regenerate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(2).TakeWhile(word => word != "--").ToArray();

        Assert.Equal(FlagValue(gateFlags, "--slot"), FlagValue(regenerateFlags, "--slot"));
        Assert.Equal(FlagValue(gateFlags, "--slot-wait"), FlagValue(regenerateFlags, "--slot-wait"));
    }

    /// <summary>
    /// No gate other than <c>test</c> takes a slot: builds, format checks and the dash tests are never serialized.
    /// </summary>
    /// <remarks>
    /// The slot is for the full .NET suite, the run that saturates every core. Serializing a build or a format check would
    /// make a review wait behind a test run it does not compete with.
    /// </remarks>
    [Fact]
    public void OnlyTheDotnetTestGateTakesASlot()
    {
        List<string> slotted = [];
        foreach (YamlNode node in ((YamlSequenceNode)Child((YamlMappingNode)Child(LoadRoot(), "review")!, "gates")!).Children)
        {
            YamlMappingNode gate = (YamlMappingNode)node;
            if (Child(gate, "run") is YamlSequenceNode argv &&
                argv.Children.Any(item => ((YamlScalarNode)item).Value == "--slot"))
            {
                slotted.Add(Scalar(gate, "id") ?? "(no id)");
            }
        }

        Assert.Equal(["test"], slotted);
    }

    private static string? FlagValue(string[] words, string flag)
    {
        int index = Array.IndexOf(words, flag);
        return index >= 0 && index + 1 < words.Length ? words[index + 1] : null;
    }

    private static string? GateScalar(string gateId, string key)
    {
        YamlMappingNode review = (YamlMappingNode)Child(LoadRoot(), "review")!;
        foreach (YamlNode node in ((YamlSequenceNode)Child(review, "gates")!).Children)
        {
            YamlMappingNode gate = (YamlMappingNode)node;
            if (Scalar(gate, "id") == gateId)
                return Scalar(gate, key);
        }

        return null;
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
