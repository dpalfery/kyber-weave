using KyberWeave.Application.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The repository doctor's cases whose subject is the disk itself: an index lock another handle holds exclusively,
/// and directories whose POSIX mode refuses a write. Neither can be shown over an in-memory file system, so each
/// runs against a real temp repository. The rest of the doctor's decisions are in <c>RepositoryDoctorTests</c>.
/// </summary>
public sealed class RepositoryDoctorRealFileSystemTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose()
    {
        _root.Dispose();
    }

    private string Root => _root.Path;

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    /// <summary>
    /// A second doctor on a root another doctor holds waits up to the index timeout and then exits 1 with
    /// <c>codegraph.index</c> busy.
    /// </summary>
    /// <remarks>
    /// Skipped, not weakened. D10 places the lock file in the OS temp directory keyed by a hash of the root, but the
    /// contract fixes no lock seam and no path, so a test cannot hold the lock. It needs the provisioner's lock path
    /// exposed or injected.
    /// </remarks>
    [Fact]
    public void Doctor_ConcurrentLockBusy_Exits1Busy()
    {
        Directory.CreateDirectory(IndexDirectory);
        string lockDirectory = Path.GetTempPath();
        string lockPath = CodeGraphIndexRules.LockPathFor(Root, lockDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            DoctorReport report = DoctorTestServices.Doctor.Run(
                Root,
                DoctorMode.Provision,
                new DoctorOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), "codegraph", "kyber-weave-mcp-absent"),
                executor,
                _ => null,
                lockDirectory);

            Assert.Equal(1, report.ExitCode);
            DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
            Assert.False(index.Passed);
            Assert.Contains("busy", index.Reason, StringComparison.Ordinal);
            DoctorHarness.AssertNoCall(executor, "index");
        }
    }

    /// <summary>
    /// A home directory variable set to the empty string does not crash the run: it simply cannot match the
    /// repository root, so the home refusal does not apply and the run proceeds to the checks.
    /// </summary>
    /// <remarks>
    /// <c>HOME</c> can be set to an empty value in CI and sandboxed shells, and <c>Path.GetFullPath("")</c> throws.
    /// A blank value is not a home directory, so the refusal must be skipped rather than raised as an exception that
    /// escapes before any check runs.
    /// </remarks>

    /// <summary>
    /// A lock file that cannot be opened for a reason other than contention fails <c>codegraph.index</c> with exit 1
    /// and a reason naming the lock, rather than escaping as an access error.
    /// </summary>
    /// <remarks>
    /// The lock directory is made read-only, so creating the lock file is denied. The test skips when the process can
    /// still write there — a root user ignores the mode — and restores the mode before the temp directory is removed.
    /// </remarks>
    [Fact]
    public void Doctor_LockFileUnopenable_Exits1WithReason()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX directory modes decide this case; Windows has no equivalent read-only directory bit.");
        }

        Directory.CreateDirectory(IndexDirectory);
        TempDirectory lockDirectory = new();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lockDirectory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            string probe = Path.Combine(lockDirectory.Path, "probe");
            try
            {
                File.Create(probe).Dispose();
                File.Delete(probe);
                Assert.Skip("This process can write to a read-only directory (running as root), so the lock cannot fail.");
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the directory denies creation, which is the case under test.
            }

            ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);
            DoctorReport report = DoctorTestServices.Doctor.Run(
                Root,
                DoctorMode.Provision,
                DoctorHarness.Options("kyber-weave-mcp-absent"),
                executor,
                _ => null,
                lockDirectory.Path);

            Assert.Equal(1, report.ExitCode);
            DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
            Assert.False(index.Passed);
            Assert.Contains("lock", index.Reason!, StringComparison.OrdinalIgnoreCase);
            DoctorHarness.AssertNoCall(executor, "index");
            DoctorHarness.AssertNoCall(executor, "init");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    lockDirectory.Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            lockDirectory.Dispose();
        }
    }

    /// <summary>
    /// A path argument that is not a usable path is a client-input refusal with exit 2, not an exception. The
    /// 0/1/2/3 exit contract is what callers branch on, so an unusable argument must stay inside it.
    /// </summary>

    /// <summary>
    /// An unwritable <c>.codegraph/</c> fails with exit 1 and a remedy, and is never deleted. The directory is made
    /// read-only, so the test restores its mode before the temp directory is removed.
    /// </summary>
    /// <remarks>
    /// D10: doctor never deletes a user's index, because a deletion would silently throw away a large index. The remedy
    /// is for a human to run, not for doctor to do.
    /// </remarks>
    [Fact]
    public void Doctor_CodegraphDirUnwritable_Exits1WithRemedy_DoesNotDelete()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX directory modes decide this case; Windows has no equivalent read-only directory bit.");
        }

        Directory.CreateDirectory(IndexDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(IndexDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            ScriptedProcessExecutor executor = DoctorHarness.Executor(Root);

            DoctorReport report = DoctorTestServices.Doctor.Run(
                Root,
                DoctorMode.Provision,
                DoctorHarness.Options("kyber-weave-mcp-absent"),
                executor,
                _ => null);

            Assert.Equal(1, report.ExitCode);
            DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
            Assert.False(index.Passed);
            Assert.False(string.IsNullOrWhiteSpace(index.Fix));
            Assert.True(Directory.Exists(IndexDirectory), "The doctor must never delete .codegraph/.");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    IndexDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>
    /// The ensure run spends exactly one <c>codegraph explore</c> call and exactly one docs tool call,
    /// so the command proves both tools work without becoming a second retrieval loop.
    /// </summary>
    /// <remarks>
    /// D22: one <c>codegraph explore</c>, one <c>kw-docs</c> call. A probe that queried repeatedly
    /// would make every worker start cost several index and corpus reads, which is the run-speed
    /// problem this slice exists to fix rather than to move.
    /// </remarks>
}
