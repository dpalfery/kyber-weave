using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The optional gate keys: <c>include</c> and <c>os</c> on council gates, <c>working-directory</c>
/// on both gate kinds, and the unknown-key warning that stops a misspelt key from being dropped
/// silently. Each refusal is raised where the operator wrote the gate, because a gate that only
/// fails when it runs surfaces in the middle of someone else's task.
/// </summary>
/// <remarks>
/// Working-directory checks need a real repository root, so they write a
/// <c>.kyber-weave/kyber-weave.yml</c> into a temp tree and load it through the path a command
/// takes. Refusals are asserted through <see cref="KyberWeaveConfigLoader.TryLoad(string, string?)"/>
/// because that is where a refusal becomes KW-CONFIG-001 naming the gate.
/// </remarks>
public sealed class ReviewGateKeysConfigTests : IDisposable
{
    private const string CouncilGateId = "build";
    private const string TaskGateId = "typecheck";
    private const string TaskAgent = "typescript-dev";

    private readonly TempDirectory _root = new();
    private readonly TempDirectory _outside = new();

    /// <summary>Deletes the repository and outside temp directories.</summary>
    public void Dispose()
    {
        _root.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void CouncilGateIncludeAndOsAreReadAsWritten()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                  include: ["**/*.cs"]
                  os: unix
            """).Review;

        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal(["**/*.cs"], gate.Include);
        Assert.Equal("unix", gate.Os);
    }

    /// <summary>
    /// A council gate that names neither key must keep the defaults, so every host that
    /// upgrades without touching its configuration runs exactly the gates it ran before.
    /// </summary>
    [Fact]
    public void CouncilGateWithoutIncludeOsOrWorkingDirectoryKeepsTheDefaults()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
            """).Review;

        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Empty(gate.Include);
        Assert.Null(gate.Os);
        Assert.Null(gate.WorkingDirectory);
    }

    [Fact]
    public void CouncilGateRefusesAnOsValueThatIsNotRecognised()
    {
        YamlException thrown = Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                  os: macos
            """));

        Assert.Contains("'macos'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not recognised", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CouncilWorkingDirectoryIsKeptNormalizedAndRepositoryRelative()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "tools", "lint"));
        WriteConfig(CouncilYaml("./tools/lint/"));

        ReviewConfig config = KyberWeaveConfigLoader.Load(_root.Path).Review;

        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal("tools/lint", gate.WorkingDirectory?.Replace('\\', '/'));
    }

    [Fact]
    public void TaskGateWorkingDirectoryIsKeptNormalizedAndRepositoryRelative()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "web"));
        WriteConfig(TaskYaml("./web/"));

        ReviewConfig config = KyberWeaveConfigLoader.Load(_root.Path).Review;

        ReviewTaskGate gate = Assert.Single(config.TaskGates[TaskAgent]);
        Assert.Equal("web", gate.WorkingDirectory?.Replace('\\', '/'));
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../outside")]
    [InlineData("missing")]
    public void CouncilWorkingDirectoryThatIsAbsoluteEscapingOrMissingIsRefusedNamingTheGate(string workingDirectory)
    {
        WriteConfig(CouncilYaml(workingDirectory));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), CouncilGateId);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../outside")]
    [InlineData("missing")]
    public void TaskGateWorkingDirectoryThatIsAbsoluteEscapingOrMissingIsRefusedNamingTheGate(string workingDirectory)
    {
        WriteConfig(TaskYaml(workingDirectory));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), TaskGateId);
    }

    [Fact]
    public void CouncilWorkingDirectoryThatLeavesTheRootThroughASymlinkIsRefusedNamingTheGate()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "linked"), _outside.Path);
        WriteConfig(CouncilYaml("linked"));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), CouncilGateId);
    }

    [Fact]
    public void TaskGateWorkingDirectoryThatLeavesTheRootThroughASymlinkIsRefusedNamingTheGate()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "linked"), _outside.Path);
        WriteConfig(TaskYaml("linked"));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), TaskGateId);
    }

    /// <summary>
    /// <c>{changed}</c> holds repository-relative paths, so from any other directory it would
    /// name the wrong files. The loader refuses the combination rather than rebasing paths silently.
    /// </summary>
    [Fact]
    public void CouncilGateWithChangedTokenAndWorkingDirectoryIsRefusedNamingTheGate()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "web"));
        WriteConfig(CouncilYaml("web", """[npx, tsc, "{changed}"]"""));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), CouncilGateId);
    }

    [Fact]
    public void TaskGateWithChangedTokenAndWorkingDirectoryIsRefusedNamingTheGate()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "web"));
        WriteConfig(TaskYaml("web", """[npx, tsc, "{changed}"]"""));

        AssertRefused(KyberWeaveConfigLoader.TryLoad(_root.Path), TaskGateId);
    }

    /// <summary>
    /// A misspelt key is ignored by the typed reader, so without a warning the operator's
    /// intent vanishes. The load still succeeds: a warning must not break a host.
    /// </summary>
    [Fact]
    public void AnUnknownCouncilGateKeyIsReportedAsOneWarningNamingTheKeyAndGate()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                  includes: ["**/*.cs"]
            """).Review;

        string warning = Assert.Single(config.Warnings);
        Assert.Contains("includes", warning, StringComparison.Ordinal);
        Assert.Contains("build", warning, StringComparison.Ordinal);
        Assert.Equal(CouncilGateId, Assert.Single(config.Gates).Id);
    }

    [Fact]
    public void AnUnknownTaskGateKeyIsReportedAsOneWarningNamingTheKeyAndAgent()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              task-gates:
                typescript-dev:
                  - id: format
                    run: [dotnet, format]
                    timeout-seconds: 120
            """).Review;

        string warning = Assert.Single(config.Warnings);
        Assert.Contains("timeout-seconds", warning, StringComparison.Ordinal);
        Assert.Contains(TaskAgent, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void GateKeysThatAreAllKnownProduceNoWarning()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                  blocking: true
                  timeout-seconds: 600
                  include: ["**/*.cs"]
                  os: windows
              task-gates:
                typescript-dev:
                  - id: format
                    include: ["**/*.ts"]
                    os: unix
                    run: [npx, prettier, --check]
                    fix: [npx, prettier, --write]
                    blocking: false
                    timeout: 300
            """).Review;

        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void ProductDefaultsDeclareNoGatesNoTaskGatesAndNoWarnings()
    {
        Assert.Empty(ReviewConfig.ProductDefaults.Gates);
        Assert.Empty(ReviewConfig.ProductDefaults.TaskGates);
        Assert.Empty(ReviewConfig.ProductDefaults.Warnings);
    }

    private static string CouncilYaml(string workingDirectory, string run = "[dotnet, build]") =>
        $"review:\n  gates:\n    - id: {CouncilGateId}\n      run: {run}\n      working-directory: {workingDirectory}\n";

    private static string TaskYaml(string workingDirectory, string run = "[npx, tsc, --noEmit]") =>
        $"review:\n  task-gates:\n    {TaskAgent}:\n      - id: {TaskGateId}\n        run: {run}\n        working-directory: {workingDirectory}\n";

    private void WriteConfig(string yaml)
    {
        string configDirectory = Path.Combine(_root.Path, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), yaml);
    }

    private static void AssertRefused(KyberWeaveConfigLoadResult result, string gateId)
    {
        Assert.False(result.Success);
        Assert.Contains(gateId, result.Error ?? string.Empty, StringComparison.Ordinal);
    }
}
