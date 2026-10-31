using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for how <c>scripts/proc-guard.sh</c> treats .NET build servers after a command.
/// </summary>
/// <remarks>
/// <para>
/// <c>dotnet build</c> and <c>dotnet test</c> leave VBCSCompiler and MSBuild reusable nodes alive on
/// purpose. If the guard counted them as leftovers, every .NET run under the watchdog would exit 87
/// even when all tests passed. These tests pin that the benign servers are tolerated once shutdown
/// was attempted, and that a real leftover next to them still fails the run.
/// </para>
/// <para>
/// Safety: every case runs <c>sh -c "exit 0"</c>, which exits at once. The process table is a fixture
/// (<c>KW_PROC_GUARD_PS</c>) and the dotnet executable is a fake (<c>KW_PROC_GUARD_DOTNET</c>), so no
/// case starts a real build server, node, or any long-lived child.
/// </para>
/// </remarks>
public sealed class ProcGuardDotnetServersTests
{
    private const int LeftoversExitCode = 87;
    private const int BenignServerPidA = 2147483021;
    private const int BenignServerPidB = 2147483022;
    private const int NodeOrphanPid = 2147483023;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A command that exits 0 and leaves only VBCSCompiler and MSBuild servers must keep exit code 0.
    /// </summary>
    [Fact]
    public void RunWhenOnlyDotnetBuildServersSurvive_PassesCommandExitCodeThroughAndNotesThem()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard("ps-dotnet-servers.sh");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(BenignServerPidA.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
        Assert.Contains(BenignServerPidB.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A node orphan next to a build server must still exit 87, and only the node orphan is listed as a leftover.
    /// </summary>
    [Fact]
    public void RunWhenNodeOrphanSurvivesBesideBuildServer_Exits87ListingOnlyTheOrphan()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard("ps-dotnet-servers-node-orphan.sh");
        string[] leftoverLines = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("note:", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(leftoverLines, line => line.Contains(NodeOrphanPid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.DoesNotContain(leftoverLines, line => line.Contains(BenignServerPidA.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Process-guard script tests require /bin/bash on a POSIX environment.");
        }
    }

    private static string FixturePath(string fixtureName)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests", "Fixtures", "proc-guard", fixtureName);
    }

    /// <summary>
    /// Runs the guard around a command that exits 0 at once, with a fixture process table and a fake dotnet.
    /// </summary>
    private static ProcessResult RunGuard(string psFixtureName)
    {
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "proc-guard.sh"));
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");
        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath(psFixtureName);
        startInfo.Environment["KW_PROC_GUARD_DOTNET"] = FixturePath("fake-dotnet.sh");

        return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
    }
}
