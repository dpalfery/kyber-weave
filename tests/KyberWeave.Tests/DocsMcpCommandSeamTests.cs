using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The three <c>docs mcp-*</c> verbs reach every outside resource through a seam, so a unit test states the tree in
/// memory and asserts what the operator read without a temporary directory or a captured process console.
/// </summary>
/// <remarks>
/// What each case proves is the seam and not a restatement of the wiring engine: the root is resolved by the path
/// seam against the environment seam's directory, harness presence is answered by the file-system seam, and every
/// line goes to the console the constructor was given.
/// </remarks>
public sealed class DocsMcpCommandSeamTests
{
    private const string ServerName = "kw-docs";

    /// <summary>A fully qualified base the path seam accepts on every platform.</summary>
    private static readonly string Anchor = OperatingSystem.IsWindows() ? @"C:\kw" : "/kw";

    private static string Root => Anchor + "/repo";

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeMcpWiringService _wiring = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Anchor);

    public DocsMcpCommandSeamTests()
    {
        _configs.WriteRepositoryConfig(Root, Declaration());
    }

    /// <summary>
    /// A harness is only as satisfied as its own file: the retired-key check is scoped to the named harness, so a
    /// proven old key in that file is not hidden by another harness's clean scan.
    /// </summary>
    [Fact]
    public void ASatisfiedCheckScopesItsRetiredKeyScanToTheNamedHarness()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText($"{Root}/.mcp.json", """{"mcpServers": {}}""");

        _ = Wire("repo", targets: "claude-code");

        Assert.Equal(["claude-code"], _wiring.ScanHarnesses);
    }

    /// <summary>
    /// The migrate verb forwards the targets the operator named, so a named harness is the one the rename engine is
    /// asked about and no other.
    /// </summary>
    [Fact]
    public void AMigrateRunForwardsItsTargetsThroughTheWiringService()
    {
        DocsMcpMigrateCommand command = new(
            _wiring,
            new FakePathResolver(_files),
            _environment,
            _console);

        int exit = command.Execute(null!, new DocsMcpMigrateSettings { Path = Root, Target = "cursor" });

        Assert.Equal(0, exit);
        Assert.Equal([Root], _wiring.MigratedRoots);
        Assert.Equal<IReadOnlyList<string>>(["cursor"], _wiring.MigrateTargets[0]);
    }

    /// <summary>
    /// A relative root is resolved against the directory the environment seam names, so the answer does not depend
    /// on where the process happens to have been started.
    /// </summary>
    [Fact]
    public void AWirePreviewResolvesTheRootThroughThePathAndEnvironmentSeams()
    {
        int exit = Wire("repo", targets: "");

        Assert.Equal(0, exit);
        Assert.Equal([Root], _wiring.InspectedRoots);
    }

    /// <summary>
    /// A root the path seam refuses is reported through the configuration failure code, and nothing is read: the
    /// command prints the diagnostic to its console rather than letting the seam's exception escape.
    /// </summary>
    [Fact]
    public void AWireRunWhoseRootThePathSeamRefusesReportsTheConfigurationCodeAndReadsNothing()
    {
        int exit = Wire("", targets: "");

        Assert.Equal(1, exit);
        Assert.Contains("KW-CONFIG-001", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_wiring.InspectedRoots);
        Assert.Empty(_configs.LoadedRoots);
    }

    /// <summary>
    /// A harness marker that exists only in the injected file system puts the harness in play, so the preview does
    /// not name it among the writable harnesses no marker can detect.
    /// </summary>
    [Fact]
    public void APreviewFindsAHarnessInPlayThroughTheFileSystemSeam()
    {
        _files.CreateDirectory($"{Root}/.factory");

        _ = Wire("repo", targets: "");

        Assert.DoesNotContain("nothing named '.factory' was found", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no marker and no file in the injected file system, the same harness is named as one only a target can
    /// write, which is the preview's whole purpose.
    /// </summary>
    [Fact]
    public void APreviewWithNoMarkerInTheFileSystemSeamNamesTheWritableHarnessItCannotDetect()
    {
        _ = Wire("repo", targets: "");

        Assert.Contains("nothing named '.factory' was found", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named harness whose configuration file the file-system seam reports as present is reported as nothing to
    /// write, and the repair is never reached: presence came from the seam, not from the developer's disk.
    /// </summary>
    [Fact]
    public void ANamedTargetWhoseFileTheFileSystemSeamReportsIsSatisfiedWithoutARepair()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText($"{Root}/.mcp.json", """{"mcpServers": {}}""");

        int exit = Wire("repo", targets: "claude-code");

        Assert.Equal(0, exit);
        Assert.Empty(_wiring.RepairedRoots);
        Assert.Contains("nothing to write", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same named harness without the file in the seam's file system is handed to the repair, so the two cases
    /// differ only in what the seam says.
    /// </summary>
    [Fact]
    public void ANamedTargetWhoseFileTheFileSystemSeamReportsAbsentIsHandedToTheRepair()
    {
        int exit = Wire("repo", targets: "claude-code");

        Assert.Equal(0, exit);
        Assert.Equal([Root], _wiring.RepairedRoots);
    }

    /// <summary>
    /// The removal verb without a target lists the receipt it reads through its seams, and says there is none when
    /// the in-memory tree holds no receipt — nothing is written and no disk is touched.
    /// </summary>
    [Fact]
    public void AnUnwirePreviewReadsTheReceiptThroughTheFileSystemSeam()
    {
        DocsMcpUnwireCommand command = new(
            _wiring,
            new McpWiringReceiptStore(_files),
            new FakePathResolver(_files),
            _environment,
            _console);

        int exit = command.Execute(null!, new DocsMcpUnwireSettings { Path = Root, Target = "" });

        Assert.Equal(0, exit);
        Assert.Contains("No receipt record", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_wiring.UnwiredRoots);
    }

    private int Wire(string path, string targets) =>
        new DocsMcpWireCommand(
            _wiring,
            _configs,
            _files,
            new FakePathResolver(_files),
            _environment,
            _console).Execute(null!, new DocsMcpWireSettings { Path = path, Target = targets });

    /// <summary>The declaration <c>docs init</c> scaffolds, naming the shipped binary.</summary>
    private static string Declaration() =>
        $"""
        mcp:
          required-servers:
            {ServerName}:
              command: ['kyber-weave-mcp', --repo-root, .]
              provenance: first-party
              level: required
        """;
}
