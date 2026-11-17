using System.Diagnostics;
using System.Reflection;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The docs server's tooling-check tool, which runs the same doctor as <c>kyber-weave doctor --ensure</c> for a caller
/// with no shell. Every child is scripted or a shell stub: no test starts a real CodeGraph or the real docs server.
/// </summary>
public sealed class McpToolingCheckToolTests : IDisposable
{
    private readonly DocFixture _fixture = new();
    private readonly TempDirectory _stubs = new();

    public McpToolingCheckToolTests()
    {
        Git(_fixture.Root, "init", "-q", "-b", "main");
        Directory.CreateDirectory(Path.Combine(_fixture.Root, ".codegraph"));
    }

    public void Dispose()
    {
        _stubs.Dispose();
        _fixture.Dispose();
    }

    private string Root => _fixture.Root;

    /// <summary>The tools over the fixture, with the doctor's children scripted and no home directory to probe.</summary>
    private DocsTools Tools(Func<IReadOnlyList<string>, ProcessResult>? codegraph = null)
    {
        IServiceProvider provider = DocsTestCommands.Services(services =>
            services.AddSingleton<IProcessEnvironment>(new DoctorTestEnvironment(false, _ => null)));
        return DocsRootRegistry.CreateTools(
            Root,
            codeGraphRefreshEnabled: false,
            DocsTestCommands.Registry(Root, path => DocsRootRegistry.CreateTools(path, codeGraphRefreshEnabled: false, registry: null, provider)),
            provider,
            toolingExecutor: DoctorHarness.Executor(Root, codegraph),
            toolingMcpExecutable: DoctorHarness.HealthyStub(_stubs.Path, Root));
    }

    private static string[] Lines(string response) =>
        response.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    [Fact]
    public void TheToolingCheckToolWritesInAClosedWorldIsIdempotentAndRequiresAProjectPath()
    {
        MethodInfo method = typeof(DocsTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == DocsTools.ToolingCheckToolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.False(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        ParameterInfo projectPath = Assert.Single(method.GetParameters());
        Assert.Equal("projectPath", projectPath.Name);
        Assert.False(projectPath.HasDefaultValue);
    }

    [Fact]
    public void AHealthyCheckoutReportsEachCheckOnOneLineAndExitsZero()
    {
        string[] lines = Lines(Tools().RunToolingCheck(Root));

        Assert.StartsWith("provenance:", lines[0], StringComparison.Ordinal);
        Assert.Equal("result: passed", lines[1]);
        Assert.Contains(lines, line => line.StartsWith("PASS codegraph.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PASS mcp.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("FAIL ", StringComparison.Ordinal));
        Assert.DoesNotContain(ToolingCheckService.ToolFailureLine, lines);
        Assert.Equal("EXIT: 0", lines[^1]);
    }

    [Fact]
    public void AnIndexThatCannotBeRepairedReportsTheFixLineToolFailureAndExitOne()
    {
        string[] lines = Lines(Tools(_ => new ProcessResult(1, string.Empty, "index is corrupt")).RunToolingCheck(Root));

        Assert.Equal("result: failed", lines[1]);
        int failure = Array.FindIndex(lines, line => line.StartsWith("FAIL codegraph.", StringComparison.Ordinal));
        Assert.True(failure >= 0, "Expected a FAIL line for a codegraph check.");
        Assert.StartsWith("  fix: ", lines[failure + 1], StringComparison.Ordinal);
        Assert.Contains(ToolingCheckService.ToolFailureLine, lines);
        Assert.Equal("EXIT: 1", lines[^1]);
    }

    [Fact]
    public void AMissingCodeGraphBinaryReportsToolFailureAndExitThree()
    {
        string[] lines = Lines(Tools(_ => throw DoctorHarness.BinaryNotFound()).RunToolingCheck(Root));

        Assert.Contains(ToolingCheckService.ToolFailureLine, lines);
        Assert.Equal("EXIT: 3", lines[^1]);
    }

    [Fact]
    public void ARepositoryThatOptedOutOfStoppingGetsWarningsExitZeroAndNoToolFailure()
    {
        _fixture.Write(".kyber-weave/kyber-weave.yml", "stop-on-tool-failure: false\n");

        string[] lines = Lines(Tools(_ => new ProcessResult(1, string.Empty, "index is corrupt")).RunToolingCheck(Root));

        Assert.Contains(lines, line => line.StartsWith("PASS codegraph.", StringComparison.Ordinal)
            && line.Contains(" warning: ", StringComparison.Ordinal));
        Assert.DoesNotContain(ToolingCheckService.ToolFailureLine, lines);
        Assert.Equal("EXIT: 0", lines[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingProjectPathIsRefusedAndRunsNothing(string projectPath)
    {
        string response = Tools(_ => throw new InvalidOperationException("the doctor must not run")).RunToolingCheck(projectPath);

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Contains("projectPath is required", response, StringComparison.Ordinal);
        Assert.DoesNotContain("EXIT:", response, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectPathOutsideTheRepositoryIsRefusedAndRunsNothing()
    {
        using TempDirectory elsewhere = new();

        string response = Tools(_ => throw new InvalidOperationException("the doctor must not run")).RunToolingCheck(elsewhere.Path);

        Assert.Contains("projectPath was refused", response, StringComparison.Ordinal);
        Assert.DoesNotContain("EXIT:", response, StringComparison.Ordinal);
    }

    private static void Git(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
