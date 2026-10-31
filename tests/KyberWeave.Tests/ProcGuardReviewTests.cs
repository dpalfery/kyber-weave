using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for review-driven hardening of <c>scripts/proc-guard.sh</c>: fail-closed root
/// validation, tolerance of non-canonical elapsed times, and the JavaScript runtime set behind
/// <c>--max-node</c>.
/// </summary>
/// <remarks>
/// Safety: the process table is a fixture (<c>KW_PROC_GUARD_PS</c>). The only child any case starts is
/// <c>sleep 5</c>, which exits by itself, and the guard kills it as soon as the cap trips.
/// </remarks>
public sealed class ProcGuardReviewTests
{
    private const int UsageExitCode = 2;
    private const int CapTrippedExitCode = 86;
    private const int LeftoversExitCode = 87;
    private const int OrphanPid = 2147483003;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A root that is not a directory must be refused, not scanned as a prefix.</summary>
    [Fact]
    public void CheckLeftovers_WhenRootIsNotADirectory_RefusesWithExit2()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard(["check-leftovers", "--root", "/nonexistent/proc-guard-review-root"], "ps-orphan.sh", null);

        Assert.Equal(UsageExitCode, result.ExitCode);
    }

    /// <summary>The filesystem root would match nearly every command line, so it is refused.</summary>
    [Fact]
    public void CheckLeftovers_WhenRootIsFilesystemRoot_RefusesWithExit2()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard(["check-leftovers", "--root", "/"], "ps-orphan.sh", null);

        Assert.Equal(UsageExitCode, result.ExitCode);
    }

    /// <summary>
    /// A fractional-seconds etime is unknown age: the survivor is still reported (exit 87) and no shell
    /// arithmetic error escapes.
    /// </summary>
    [Fact]
    public void CheckLeftovers_WhenEtimeIsNotCanonical_TreatsAgeAsUnknownAndStillReports()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunGuard(
            ["check-leftovers", "--root", root.Path, "--since", "1"], "ps-orphan.sh", "etime-malformed.sh");
        string combined = result.StandardOutput + result.StandardError;

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(OrphanPid.ToString(CultureInfo.InvariantCulture), combined, StringComparison.Ordinal);
        Assert.DoesNotContain("syntax error", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("value too great", combined, StringComparison.Ordinal);
    }

    /// <summary>bun, nodejs and tsx count toward <c>--max-node</c> like node does.</summary>
    [Fact]
    public void RunWhenBunNodejsAndTsxReachMaxNode_Exits86NamingTheCap()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard(["run", "--max-node", "3", "--", "sleep", "5"], "ps-node-family-3.sh", null);

        Assert.Equal(CapTrippedExitCode, result.ExitCode);
        Assert.Contains("max-node", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>Several trailing slashes still name the filesystem root, so they are refused too.</summary>
    [Theory]
    [InlineData("//")]
    [InlineData("///")]
    public void CheckLeftovers_WhenRootIsFilesystemRootWithExtraSlashes_RefusesWithExit2(string root)
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard(["check-leftovers", "--root", root], "ps-orphan.sh", null);

        Assert.Equal(UsageExitCode, result.ExitCode);
    }

    /// <summary>
    /// A clock that fails at the leftover scan must make the run exit 2 rather than fall back to an elapsed time
    /// of zero, which would drop a genuinely old orphan and let the run report success.
    /// </summary>
    /// <remarks>
    /// The fake dotnet runs only after the wrapped command ends and creates a marker file. From then on the fake
    /// date fails, so the clock breaks exactly at the scan and never inside the poll loop.
    /// </remarks>
    [Fact]
    public void RunWhenClockFailsAtLeftoverScan_Exits2InsteadOfHidingOldOrphan()
    {
        SkipIfBashUnavailable();
        using TempDirectory directory = new();
        Dictionary<string, string> environment = new()
        {
            ["KW_PROC_GUARD_DOTNET"] = FixturePath("fake-dotnet-creates-marker.sh"),
            ["KW_PROC_GUARD_DATE"] = FixturePath("fake-date-fails-after-marker.sh"),
            ["KW_PROC_GUARD_TEST_MARKER"] = Path.Combine(directory.Path, "scan-started")
        };

        ProcessResult result = RunGuard(["run", "--", "sh", "-c", "exit 0"], "ps-orphan.sh", "etime-old.sh", environment);

        Assert.Equal(UsageExitCode, result.ExitCode);
        Assert.Contains("clock", result.StandardError, StringComparison.Ordinal);
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

    private static ProcessResult RunGuard(
        string[] guardArguments,
        string psFixtureName,
        string? etimeFixtureName,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
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
        foreach (string argument in guardArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath(psFixtureName);
        if (etimeFixtureName is not null)
        {
            startInfo.Environment["KW_PROC_GUARD_ETIME"] = FixturePath(etimeFixtureName);
        }

        if (extraEnvironment is not null)
        {
            foreach (KeyValuePair<string, string> pair in extraEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
    }
}
