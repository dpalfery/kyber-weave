using KyberWeave.Contracts.Repositories;

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
/// Working-directory checks need a repository root, so they write a
/// <c>.kyber-weave/kyber-weave.yml</c> into an in-memory tree and load it through the path a command
/// takes. Refusals are asserted through <see cref="Configuration.TryLoad(string, string?)"/>
/// because that is where a refusal becomes KW-CONFIG-001 naming the gate.
/// </remarks>
public sealed class ReviewGateKeysConfigTests : IDisposable
{
    private const string CouncilGateId = "build";
    private const string TaskGateId = "typecheck";
    private const string TaskAgent = "typescript-dev";

    private readonly InMemoryReviewServices _services = new();
    private readonly string _root;
    private readonly string _outside;

    public ReviewGateKeysConfigTests()
    {
        _root = Path.Combine(_services.Root, "repo");
        _outside = Path.Combine(_services.Root, "outside");
        _services.Files.CreateDirectory(_root);
        _services.Files.CreateDirectory(_outside);
    }

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    private IKyberWeaveConfigRepository Configuration => _services.Get<IKyberWeaveConfigRepository>();

    [Fact]
    public void CouncilGateIncludeAndOsAreReadAsWritten()
    {
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
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
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
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
        YamlException thrown = Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigAssembler.FromYaml("""
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
        _services.Files.CreateDirectory(Path.Combine(_root, "tools", "lint"));
        WriteConfig(CouncilYaml("./tools/lint/"));

        ReviewConfig config = Configuration.Load(_root).Review;

        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal("tools/lint", gate.WorkingDirectory?.Replace('\\', '/'));
    }

    [Fact]
    public void TaskGateWorkingDirectoryIsKeptNormalizedAndRepositoryRelative()
    {
        _services.Files.CreateDirectory(Path.Combine(_root, "web"));
        WriteConfig(TaskYaml("./web/"));

        ReviewConfig config = Configuration.Load(_root).Review;

        ReviewTaskGate gate = Assert.Single(config.TaskGates[TaskAgent]);
        Assert.Equal("web", gate.WorkingDirectory?.Replace('\\', '/'));
    }

    [Theory]
    [InlineData(" \" /web\"")]
    [InlineData(" \" /\"")]
    [InlineData(" \" C:/web\"")]
    public void WorkingDirectoryWithLeadingWhitespaceAbsoluteIsRefused(string workingDirectory)
    {
        _services.Files.CreateDirectory(Path.Combine(_root, "web"));
        WriteConfig(CouncilYaml(workingDirectory));

        AssertRefused(Configuration.TryLoad(_root), CouncilGateId);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../outside")]
    [InlineData("missing")]
    public void CouncilWorkingDirectoryThatIsAbsoluteEscapingOrMissingIsRefusedNamingTheGate(string workingDirectory)
    {
        WriteConfig(CouncilYaml(workingDirectory));

        AssertRefused(Configuration.TryLoad(_root), CouncilGateId);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../outside")]
    [InlineData("missing")]
    public void TaskGateWorkingDirectoryThatIsAbsoluteEscapingOrMissingIsRefusedNamingTheGate(string workingDirectory)
    {
        WriteConfig(TaskYaml(workingDirectory));

        AssertRefused(Configuration.TryLoad(_root), TaskGateId);
    }

    [Fact]
    public void CouncilWorkingDirectoryThatLeavesTheRootThroughASymlinkIsRefusedNamingTheGate()
    {
        _services.Files.CreateDirectoryLink(Path.Combine(_root, "linked"), _outside);
        WriteConfig(CouncilYaml("linked"));

        AssertRefused(Configuration.TryLoad(_root), CouncilGateId);
    }

    [Fact]
    public void TaskGateWorkingDirectoryThatLeavesTheRootThroughASymlinkIsRefusedNamingTheGate()
    {
        _services.Files.CreateDirectoryLink(Path.Combine(_root, "linked"), _outside);
        WriteConfig(TaskYaml("linked"));

        AssertRefused(Configuration.TryLoad(_root), TaskGateId);
    }

    /// <summary>
    /// <c>{changed}</c> holds repository-relative paths, so from any other directory it would
    /// name the wrong files. The loader refuses the combination rather than rebasing paths silently.
    /// </summary>
    [Fact]
    public void CouncilGateWithChangedTokenAndWorkingDirectoryIsRefusedNamingTheGate()
    {
        _services.Files.CreateDirectory(Path.Combine(_root, "web"));
        WriteConfig(CouncilYaml("web", """[npx, tsc, "{changed}"]"""));

        AssertRefused(Configuration.TryLoad(_root), CouncilGateId);
    }

    [Fact]
    public void TaskGateWithChangedTokenAndWorkingDirectoryIsRefusedNamingTheGate()
    {
        _services.Files.CreateDirectory(Path.Combine(_root, "web"));
        WriteConfig(TaskYaml("web", """[npx, tsc, "{changed}"]"""));

        AssertRefused(Configuration.TryLoad(_root), TaskGateId);
    }

    /// <summary>
    /// <c>fail-on-output</c> is opt-in: a gate that omits it keeps judging by exit code alone, so
    /// every host that upgrades without touching its configuration runs the gates it ran before.
    /// </summary>
    [Fact]
    public void FailOnOutputDefaultsToFalseOnCouncilAndTaskGates()
    {
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
              task-gates:
                typescript-dev:
                  - id: format
                    run: [dotnet, format]
            """).Review;

        Assert.False(Assert.Single(config.Gates).FailOnOutput);
        Assert.False(Assert.Single(config.TaskGates[TaskAgent]).FailOnOutput);
    }

    [Fact]
    public void FailOnOutputIsReadOnCouncilAndTaskGatesWithoutAWarning()
    {
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
            review:
              gates:
                - id: fmt
                  run: [gofmt, -l, .]
                  fail-on-output: true
              task-gates:
                typescript-dev:
                  - id: format
                    run: [gofmt, -l, "{changed}"]
                    fix: [gofmt, -w, "{changed}"]
                    fail-on-output: true
            """).Review;

        Assert.True(Assert.Single(config.Gates).FailOnOutput);
        Assert.True(Assert.Single(config.TaskGates[TaskAgent]).FailOnOutput);
        Assert.Empty(config.Warnings);
    }

    /// <summary>
    /// <c>concurrent</c> is opt-in: a gate that omits it keeps holding up the gates declared after it,
    /// so a host that upgrades without touching its configuration runs its gates in the order it always did.
    /// </summary>
    [Fact]
    public void ConcurrentDefaultsToFalseAndIsReadOnACouncilGateWithoutAWarning()
    {
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                - id: analyze
                  run: [dotnet, build, -warnaserror]
                  concurrent: true
            """).Review;

        Assert.False(config.Gates[0].Concurrent);
        Assert.True(config.Gates[1].Concurrent);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void ParallelismDefaultsToTwoAndReadsAHostValue()
    {
        Assert.Equal(2, KyberWeaveConfigAssembler.FromYaml("review:\n  gates: []\n").Review.Parallelism);
        Assert.Equal(3, KyberWeaveConfigAssembler.FromYaml("review:\n  parallelism: 3\n").Review.Parallelism);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ParallelismBelowOneIsRefusedWhereTheOperatorWroteIt(string value)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => KyberWeaveConfigAssembler.FromYaml($"review:\n  parallelism: {value}\n"));

        Assert.Contains("review.parallelism must be greater than zero", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A misspelt key is ignored by the typed reader, so without a warning the operator's
    /// intent vanishes. The load still succeeds: a warning must not break a host.
    /// </summary>
    [Fact]
    public void AnUnknownCouncilGateKeyIsReportedAsOneWarningNamingTheKeyAndGate()
    {
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
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
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
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
        ReviewConfig config = KyberWeaveConfigAssembler.FromYaml("""
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
        string configDirectory = Path.Combine(_root, ".kyber-weave");
        _services.Files.CreateDirectory(configDirectory);
        _services.Files.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), yaml);
    }

    private static void AssertRefused(KyberWeaveConfigLoadResult result, string gateId)
    {
        Assert.False(result.Success);
        Assert.Contains(gateId, result.Error ?? string.Empty, StringComparison.Ordinal);
    }
}
