using KyberWeave.Tests.Configuration;

using System.Diagnostics;

using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Processes;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Mcp;
using Xunit;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Integration.Tests;

/// <summary>Proves that project-local MCP bindings do not cross a shared parent directory.</summary>
public sealed class McpRepositoryRootTests : IDisposable
{
    /// <summary>The expected-root assertion variable, spelled as the plan's D3 contract.</summary>
    private const string ExpectRootEnvironmentVariable = "KYBER_WEAVE_EXPECT_ROOT";

    private const string InitializeRequests =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"check\",\"version\":\"0\"}}}\n"
        + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n";

    private readonly TempDirectory _parent = new();

    [Fact]
    public void DocsInitEstablishesTheCurrentDirectoryAsAnMcpRoot()
    {
        DocsTestCommands.Scaffolding.Scaffold(_parent.Path);

        string resolved = RepositoryRootResolver.Resolve(ConfigFileReader.Repository, [], _parent.Path, null);

        Assert.Equal(Path.GetFullPath(_parent.Path), resolved);
    }

    [Fact]
    public void ProjectLocalBindingsKeepQueriesInsideTheirOwnRepository()
    {
        string first = Path.Combine(_parent.Path, "first-repository");
        string second = Path.Combine(_parent.Path, "second-repository");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        DocsTestCommands.Scaffolding.Scaffold(first, owner: "first-team");
        DocsTestCommands.Scaffolding.Scaffold(second, owner: "second-team");
        WriteMarker(first, "first-only-marker");
        WriteMarker(second, "second-only-marker");

        string firstRoot = RepositoryRootResolver.Resolve(ConfigFileReader.Repository, ["--repo-root", "."], first, null);
        string secondRoot = RepositoryRootResolver.Resolve(ConfigFileReader.Repository, ["--repo-root", "."], second, null);

        ExploreAnswer firstAnswer = CreateHost(firstRoot).Current().Explore("first-only-marker");
        ExploreAnswer secondAnswer = CreateHost(secondRoot).Current().Explore("second-only-marker");

        Assert.False(firstAnswer.IsMiss);
        Assert.False(secondAnswer.IsMiss);
        Assert.Equal("docs/first-only.md", firstAnswer.SourceDocument!.RelativePath.Replace('\\', '/'));
        Assert.Equal("docs/second-only.md", secondAnswer.SourceDocument!.RelativePath.Replace('\\', '/'));
        Assert.NotEqual(firstAnswer.SourceDocument.RelativePath, secondAnswer.SourceDocument.RelativePath);
    }

    [Fact]
    public void AnUninitializedWorkingDirectoryFailsInsteadOfGuessingAParentRepository()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RepositoryRootResolver.Resolve(ConfigFileReader.Repository, [], _parent.Path, null));

        Assert.Contains("kyber-weave docs init", error.Message, StringComparison.Ordinal);
        Assert.Contains("will not guess a parent repository", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3 RED — a client can assert which absolute root it believes it is talking to, and
    /// the server refuses to serve when the bound root differs. Covers the reported failure
    /// mode where a same-named server bound to another checkout answered silently.
    /// </summary>
    [Fact]
    public void ExpectRootNamingADifferentRepositoryIsRefused()
    {
        (string first, string second) = CreateTwoInitializedRepositories();

        ExpectedRootMismatchException error = Assert.Throws<ExpectedRootMismatchException>(() =>
            RepositoryRootResolver.Resolve(
                ConfigFileReader.Repository,
                ["--repo-root", first, "--expect-root", second],
                first,
                null));

        Assert.Contains("expect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(second), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectRootEqualToTheResolvedRootIsAccepted()
    {
        (string first, _) = CreateTwoInitializedRepositories();

        string resolved = RepositoryRootResolver.Resolve(
            ConfigFileReader.Repository,
            ["--repo-root", ".", "--expect-root", first],
            first,
            null);

        Assert.Equal(Path.GetFullPath(first), resolved);
    }

    [Fact]
    public void ARelativeExpectRootResolvesAgainstTheBoundWorkingDirectory()
    {
        (string first, _) = CreateTwoInitializedRepositories();

        string resolved = RepositoryRootResolver.Resolve(
            ConfigFileReader.Repository,
            ["--repo-root", ".", "--expect-root", "."],
            first,
            null);

        Assert.Equal(Path.GetFullPath(first), resolved);
    }

    [Fact]
    public void ExpectRootFlagNamingADifferentRepositoryRefusesToServe()
    {
        (string first, string second) = CreateTwoInitializedRepositories();

        ProcessResult result = RunMcp(
            first, expectRootFlag: second, expectedRootEnvironment: null, input: string.Empty);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("KW-MCP-ROOT-002", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(second), result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectRootFlagEqualToTheRepositoryAllowsStartup()
    {
        (string first, _) = CreateTwoInitializedRepositories();

        ProcessResult result = RunMcp(
            first, expectRootFlag: first, expectedRootEnvironment: null, input: InitializeRequests);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("KW-MCP-ROOT-002", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectRootEnvironmentVariableIsHonoredWhenTheFlagIsAbsent()
    {
        (string first, string second) = CreateTwoInitializedRepositories();

        ProcessResult result = RunMcp(
            first, expectRootFlag: null, expectedRootEnvironment: second, input: string.Empty);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("KW-MCP-ROOT-002", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineExpectRootOverridesTheEnvironmentVariable()
    {
        (string first, string second) = CreateTwoInitializedRepositories();

        ProcessResult result = RunMcp(
            first, expectRootFlag: first, expectedRootEnvironment: second, input: InitializeRequests);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("KW-MCP-ROOT-002", result.StandardError, StringComparison.Ordinal);
    }

    public void Dispose() => _parent.Dispose();

    private static DocumentIndexHost CreateHost(string root)
    {
        KyberWeaveConfig config = ConfigFileReader.Load(root);
        return new DocumentIndexHost(
            root,
            () => CodeGraphResolverAdapter.ForRepository(root),
            () => RealDocumentLoader.Create(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
    }

    private static void WriteMarker(string root, string marker)
    {
        File.WriteAllText(
            Path.Combine(root, "docs", marker.Replace("-marker", string.Empty, StringComparison.Ordinal) + ".md"),
            $"# {marker}\n\nThis repository owns {marker}.\n");
    }

    private (string First, string Second) CreateTwoInitializedRepositories()
    {
        string first = Path.Combine(_parent.Path, "first-repository");
        string second = Path.Combine(_parent.Path, "second-repository");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        DocsTestCommands.Scaffolding.Scaffold(first, owner: "first-team");
        DocsTestCommands.Scaffolding.Scaffold(second, owner: "second-team");
        return (first, second);
    }

    /// <summary>
    /// Launches the shipped MCP executable so the refuse contract is asserted where a client
    /// sees it: the exit code and the <c>KW-MCP-ROOT-002</c> line on stderr.
    /// </summary>
    /// <remarks>
    /// A refusing server exits before it reads stdin, so those cases pass empty input to
    /// avoid writing into a closed pipe. Starting cases send a minimal initialize so the
    /// server completes its normal lifecycle and exits zero.
    /// </remarks>
    private static ProcessResult RunMcp(
        string repository,
        string? expectRootFlag,
        string? expectedRootEnvironment,
        string input)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository,
        };
        startInfo.ArgumentList.Add(typeof(DocsTools).Assembly.Location);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(repository);
        if (expectRootFlag is not null)
        {
            startInfo.ArgumentList.Add("--expect-root");
            startInfo.ArgumentList.Add(expectRootFlag);
        }
        if (expectedRootEnvironment is not null)
        {
            startInfo.Environment[ExpectRootEnvironmentVariable] = expectedRootEnvironment;
        }

        return ProcessRunner.Run(startInfo, input, TimeSpan.FromSeconds(30));
    }
}
