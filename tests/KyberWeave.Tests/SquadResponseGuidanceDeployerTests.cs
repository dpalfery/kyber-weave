using System.Collections;
using System.Security;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     The line between a failure the guidance deploy may absorb and one it must let through, plus the
///     build-defect path a real host cannot provoke.
/// </summary>
/// <remarks>
///     A guidance file is a convenience: a host that refuses the write should still get its agents and
///     skills, so the deploy reports and returns. A failure that means this code asked for something
///     impossible is a different matter — a warning an operator cannot act on is how a defect survives
///     to the next release, so those propagate. These tests pin which side each failure falls on.
/// </remarks>
public sealed class SquadResponseGuidanceDeployerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public static TheoryData<Exception> HostFailures => new()
    {
        new IOException("disk full"),
        new DirectoryNotFoundException("no such directory"),
        new FileNotFoundException("no such file"),
        new SquadShippedGuidanceUnavailableException("this build carries no default"),
        new UnauthorizedAccessException("denied"),
        new NotSupportedException("unsupported path form"),
        new SecurityException("host policy denies the write")
    };

    public static TheoryData<Exception> ContractViolations => new()
    {
        new ArgumentException("this code passed a bad argument"),
        new InvalidOperationException("this code asked for something impossible"),

        // Created rather than raised: CA2201 forbids constructing this one directly in source, and the
        // test only needs an instance whose type sits outside the degradable policy.
        Activator.CreateInstance<OutOfMemoryException>()
    };

    /// <summary>
    ///     Everything a filesystem or a host sandbox can do to a healthy run is absorbed into a warning,
    ///     so a refused guidance write never costs the operator their deployment.
    /// </summary>
    [Theory]
    [MemberData(nameof(HostFailures))]
    public void FailuresAHostCanInflictOnTheWriteAreAbsorbed(Exception exception)
    {
        Assert.True(
            SquadResponseGuidanceDeployer.IsDegradableFailure(exception),
            $"'{exception.GetType().Name}' is something a host can do to a healthy run, so the guidance " +
            "deploy must report it and carry on rather than fail the deployment.");
    }

    /// <summary>
    ///     A failure that means the caller or this code broke its contract is not an operator problem,
    ///     and absorbing it would hide a defect behind a warning nobody can act on.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractViolations))]
    public void FailuresThatMeanThisCodeBrokeItsOwnContractPropagate(Exception exception)
    {
        Assert.False(
            SquadResponseGuidanceDeployer.IsDegradableFailure(exception),
            $"'{exception.GetType().Name}' is not a host condition, so the guidance deploy must not " +
            "convert it into a warning.");
    }

    /// <summary>
    ///     The classification only protects an operator if <c>Deploy</c> applies it where the file work
    ///     happens. A host failure raised while the existing guidance is compared, replaced or read back
    ///     has to come out as a warning with no record, not as an exception that fails the whole update.
    /// </summary>
    [Theory]
    [MemberData(nameof(HostFailures))]
    public void AHostFailureRaisedDuringTheFileWorkBecomesAWarningNotAFailedDeployment(Exception exception)
    {
        SquadResponseGuidanceDeployer deployer = DeployerOverAnOperatorEditedFile(out string path);
        List<string> warnings = [];
        SquadHomeFile? record = null;

        Exception? escaped = Record.Exception(
            () => record = deployer.Deploy(new FaultingHomeFiles(exception), warnings));

        Assert.True(
            escaped is null,
            $"'{exception.GetType().Name}' escaped the guidance deploy, so a host that refuses the file " +
            "work would fail the whole update instead of only skipping the guidance.");
        Assert.Null(record);
        string warning = Assert.Single(warnings);
        Assert.Contains(SquadResponseGuidanceDeployer.FileName, warning, StringComparison.Ordinal);
        Assert.Equal("operator text", File.ReadAllText(path));
    }

    /// <summary>
    ///     The negative control for the test above: a guard wide enough to pass it by swallowing
    ///     everything would also hide a defect in this code, so those failures must still escape.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractViolations))]
    public void AContractViolationRaisedDuringTheFileWorkStillFailsTheDeployment(Exception exception)
    {
        SquadResponseGuidanceDeployer deployer = DeployerOverAnOperatorEditedFile(out _);
        List<string> warnings = [];

        Exception? escaped = Record.Exception(
            () => deployer.Deploy(new FaultingHomeFiles(exception), warnings));

        Assert.Same(exception, escaped);
        Assert.Empty(warnings);
    }

    /// <summary>
    ///     A build that shipped without its default guidance is a packaging defect, not a disk problem.
    ///     It must still leave the deployment intact, but the warning may not tell the operator to rerun:
    ///     no rerun can add a resource the binary does not carry.
    /// </summary>
    [Fact]
    public void AShippedDefaultMissingFromTheBuildIsReportedAsABuildDefectWithoutWriting()
    {
        string home = Path.Combine(_temp.Path, "home");
        Directory.CreateDirectory(home);

        // The same exception the production reader raises when the assembly carries no such resource.
        SquadResponseGuidanceDeployer deployer = new(
            home,
            () => throw new SquadShippedGuidanceUnavailableException(
                "KyberWeave.Core does not carry its default response guidance."));
        List<string> warnings = [];

        SquadHomeFile? record = deployer.Deploy(null, warnings);

        Assert.Null(record);
        string warning = Assert.Single(warnings);
        Assert.Contains("reinstall", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rerun", warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(Path.Combine(home, SquadResponseGuidanceDeployer.FileName)),
            "A build with no default guidance must write nothing rather than an empty file.");
    }

    /// <summary>
    ///     Resolving the guidance path probes the attributes of every segment on the way to it, so a host
    ///     that denies that read fails there, ahead of all the file work the degrade policy guards. The
    ///     denial has to come out as the same warning with no record, not as an exception that fails the
    ///     whole update.
    /// </summary>
    [Fact]
    public void AGuidancePathTheProcessCannotProbeBecomesAWarningNotAFailedDeployment()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are not enforced on Windows, so a denied path probe cannot be simulated.");
            return;
        }

        string home = PhysicalHome();
        SquadResponseGuidanceDeployer deployer = new(home, () => "shipped default"u8.ToArray());
        List<string> warnings = [];
        SquadHomeFile? record = null;
        Exception? escaped;

        File.SetUnixFileMode(home, UnixFileMode.None);
        try
        {
            if (!AttributeReadIsDenied(Path.Combine(home, SquadResponseGuidanceDeployer.FileName)))
            {
                Assert.Skip("The process can probe a path beneath a mode-000 directory (running as root?), so a denied path probe cannot be simulated.");
                return;
            }

            escaped = Record.Exception(() => record = deployer.Deploy(null, warnings));
        }
        finally
        {
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(
            escaped is null,
            $"'{escaped?.GetType().Name}' escaped the guidance deploy while its path was being resolved, so a " +
            "host that denies the probe would fail the whole update instead of only skipping the guidance.");
        Assert.Null(record);
        string warning = Assert.Single(warnings);
        Assert.Contains(SquadResponseGuidanceDeployer.FileName, warning, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    ///     The negative control for the test above: a guard wide enough to pass it by absorbing whatever the
    ///     path policy raises would also absorb a guidance file that is a link out of the home root. That
    ///     is a breach of the root's boundary rather than something the host inflicted, and it has to keep
    ///     failing the deployment.
    /// </summary>
    [Fact]
    public void AGuidanceFileThatLinksOutOfTheHomeRootStillFailsTheDeployment()
    {
        string home = PhysicalHome();
        string outside = Path.Combine(_temp.Path, "outside.md");
        File.WriteAllText(outside, "text beyond the home root");
        try
        {
            File.CreateSymbolicLink(Path.Combine(home, SquadResponseGuidanceDeployer.FileName), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This host cannot create symbolic links ({exception.Message}).");
            return;
        }

        SquadResponseGuidanceDeployer deployer = new(home, () => "shipped default"u8.ToArray());
        List<string> warnings = [];

        Assert.Throws<SquadPathContainmentException>(() => deployer.Deploy(null, warnings));
        Assert.Empty(warnings);
        Assert.Equal("text beyond the home root", File.ReadAllText(outside));
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     A home state root spelled without a symbolic link. The path probe stops at the first link it
    ///     meets, so a scratch directory under macOS's <c>/var</c> alias would never reach the segment a
    ///     test needs it to be denied.
    /// </summary>
    private string PhysicalHome()
    {
        string home = Path.Combine(SquadFileSystemPathSemantics.Canonicalize(_temp.Path), "home");
        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>Whether the process is refused when it reads the attributes of <paramref name="path" />.</summary>
    private static bool AttributeReadIsDenied(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     A deployer whose guidance file exists with text Squad never shipped, so the deploy has to
    ///     consult the receipt's records before deciding what to do with it.
    /// </summary>
    private SquadResponseGuidanceDeployer DeployerOverAnOperatorEditedFile(out string path)
    {
        string home = Path.Combine(_temp.Path, "home");
        Directory.CreateDirectory(home);
        path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        File.WriteAllText(path, "operator text");

        return new SquadResponseGuidanceDeployer(home, () => "shipped default"u8.ToArray());
    }

    /// <summary>
    ///     A receipt's home-file records that fail when read. The deploy enumerates them inside the same
    ///     guarded region as every filesystem call, and no real filesystem on a CI host can be made to
    ///     raise an unsupported-path or security failure, so this is the one input that can.
    /// </summary>
    private sealed class FaultingHomeFiles(Exception fault) : IReadOnlyList<SquadHomeFile>
    {
        public int Count => 1;

        public SquadHomeFile this[int index] => throw fault;

        public IEnumerator<SquadHomeFile> GetEnumerator()
        {
            throw fault;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
