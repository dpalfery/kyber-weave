using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins how the <c>inspectcode</c> review gate is declared in <c>.kyber-weave/kyber-weave.yml</c>: it runs
/// alongside the gates declared after it, it is declared between <c>build</c> and <c>test</c>, and it runs
/// through the report wrapper without a build of its own.
/// </summary>
/// <remarks>
/// The analyzer reads the whole solution and takes minutes. Declared last and run in turn, it started only after
/// the full test suite had finished, so a review waited for both in sequence. Declared after <c>build</c> it starts
/// on a restored tree, and marked <c>concurrent</c> it overlaps the test run instead of following it. This suite
/// reads the YAML only. It starts no process.
/// </remarks>
public sealed class InspectCodeGateConcurrencyTests
{
    [Fact]
    public void TheInspectCodeGateRunsAlongsideTheGatesDeclaredAfterIt()
    {
        YamlMappingNode gate = Gates().Single(candidate => Scalar(candidate, "id") == "inspectcode");

        Assert.Equal("true", Scalar(gate, "concurrent"));
    }

    [Fact]
    public void TheInspectCodeGateIsDeclaredAfterBuildAndBeforeTheFullTestRun()
    {
        List<string?> ids = Gates().Select(gate => Scalar(gate, "id")).ToList();

        Assert.True(ids.IndexOf("build") < ids.IndexOf("inspectcode"), "inspectcode must start on a restored, built tree.");
        Assert.True(ids.IndexOf("inspectcode") < ids.IndexOf("test"), "inspectcode must start before the full test run, not after it.");
    }

    /// <summary>
    /// The analyzer's thread count is capped, because the gate it overlaps is the load-sensitive full test run.
    /// </summary>
    [Fact]
    public void TheInspectCodeGateCapsItsAnalysisThreads()
    {
        Assert.Contains("--jobs=4", InspectCodeArgv());
    }

    /// <summary>
    /// The gate runs through the report wrapper, and the wrapper watches the same file the analyzer writes.
    /// </summary>
    /// <remarks>
    /// On macOS 27.3 Beta the analyzer finishes its report and never exits. Without the wrapper the gate runs to
    /// its time limit with a finished report on disk. A wrapper watching another path would never see the report.
    /// </remarks>
    [Fact]
    public void TheInspectCodeGateRunsThroughTheReportWrapperOnTheAnalyzersOwnOutputPath()
    {
        List<string?> argv = InspectCodeArgv();
        int separator = argv.IndexOf("--");

        Assert.Equal("scripts/report-gate.sh", argv[0]);
        Assert.True(separator > 0, "The wrapper's options must end with '--' before the analyzer command.");
        Assert.Equal(["dotnet", "jb", "inspectcode"], argv.Skip(separator + 1).Take(3));

        string? watched = argv[argv.IndexOf("--report") + 1];
        Assert.Contains($"--output={watched}", argv.Skip(separator + 1));
        Assert.Equal("</Report>", argv[argv.IndexOf("--ends-with") + 1]);
    }

    /// <summary>
    /// The analyzer does not build, so the gate must not start on a tree nothing has restored: it would resolve
    /// nothing, exit zero, and read as a clean run.
    /// </summary>
    [Fact]
    public void TheInspectCodeGateSkipsItsOwnBuildAndRequiresARestoredTree()
    {
        YamlMappingNode gate = Gates().Single(candidate => Scalar(candidate, "id") == "inspectcode");
        IEnumerable<string?> required = ((YamlSequenceNode)gate.Children[new YamlScalarNode("requires-paths")]).Children
            .Select(item => ((YamlScalarNode)item).Value);

        Assert.Contains("--no-build", InspectCodeArgv());
        Assert.Contains("src/KyberWeave.Core/obj/project.assets.json", required);
    }

    /// <summary>No other gate is concurrent: the rest share the Release binaries and run in turn.</summary>
    [Fact]
    public void OnlyTheInspectCodeGateIsConcurrent()
    {
        IEnumerable<string?> concurrent = Gates()
            .Where(gate => Scalar(gate, "concurrent") == "true")
            .Select(gate => Scalar(gate, "id"));

        Assert.Equal(["inspectcode"], concurrent);
    }

    private static List<string?> InspectCodeArgv()
    {
        YamlMappingNode gate = Gates().Single(candidate => Scalar(candidate, "id") == "inspectcode");
        return ((YamlSequenceNode)gate.Children[new YamlScalarNode("run")]).Children
            .Select(item => ((YamlScalarNode)item).Value)
            .ToList();
    }

    private static List<YamlMappingNode> Gates()
    {
        YamlStream stream = [];
        using StringReader reader = new(File.ReadAllText(
            Path.Combine(KyberWeaveTestPaths.ToolRoot, ".kyber-weave", "kyber-weave.yml")));
        stream.Load(reader);

        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlMappingNode review = (YamlMappingNode)root.Children[new YamlScalarNode("review")];
        return ((YamlSequenceNode)review.Children[new YamlScalarNode("gates")]).Children
            .Cast<YamlMappingNode>()
            .ToList();
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ? (node as YamlScalarNode)?.Value : null;
}
