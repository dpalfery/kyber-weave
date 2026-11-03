using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     Checks and, when the mode allows, provisions the repository's CodeGraph index.
/// </summary>
/// <remarks>
///     <para>
///         Provisioning runs <c>codegraph init .</c> when <c>.codegraph/</c> is absent and <c>codegraph index .</c>
///         otherwise, with the repository root as the working directory. The root authorises a repository-local
///         init. Nothing is installed, and nothing is ever deleted: a damaged index is reported with a remedy a
///         human runs, because deleting a large index silently would throw that work away.
///     </para>
///     <para>
///         A cross-process lock file, keyed by a hash of the root, serialises concurrent provisioning runs. A
///         second run waits up to the index timeout and then reports the index busy, rather than racing the first
///         run's writes.
///     </para>
/// </remarks>
public static class CodeGraphProvisioner
{
    /// <summary>The directory name CodeGraph keeps its index in, at the repository root.</summary>
    public const string IndexDirectoryName = ".codegraph";

    /// <summary>How long a waiting run sleeps between attempts to take the lock.</summary>
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Returns the lock path for a root: a file in <paramref name="lockDirectory"/> named by a hash of the root.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="lockDirectory">The directory that holds lock files, normally the OS temp directory.</param>
    public static string LockPathFor(string root, string lockDirectory)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(root)));
        string key = Convert.ToHexString(digest, 0, 8).ToUpperInvariant();
        return Path.Combine(lockDirectory, $"kyber-weave-codegraph-{key}.lock");
    }

    /// <summary>Runs the CodeGraph checks, provisioning only in <see cref="DoctorMode.Provision"/>.</summary>
    /// <param name="root">The resolved repository root.</param>
    /// <param name="mode">The run's mode.</param>
    /// <param name="options">The budgets and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child. Production passes an executor bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the lock file lives.</param>
    public static CodeGraphOutcome Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory)
    {
        List<DoctorCheck> checks = [];

        if (!DetectBinary(options, executor, out bool missing, out string? binaryReason))
        {
            checks.Add(new DoctorCheck(
                "codegraph.binary",
                false,
                binaryReason,
                "install the codegraph CLI so that 'codegraph' is on PATH, then run: kyber-weave doctor --check"));
            return new CodeGraphOutcome(checks, BinaryMissing: missing);
        }

        checks.Add(new DoctorCheck("codegraph.binary", true, "found on PATH", null));

        string indexDirectory = Path.Combine(root, IndexDirectoryName);
        bool indexPresent = Directory.Exists(indexDirectory);

        if (mode == DoctorMode.Check)
        {
            checks.Add(CheckIndex(indexDirectory, indexPresent));
            return new CodeGraphOutcome(checks, BinaryMissing: false);
        }

        checks.Add(Provision(root, indexDirectory, indexPresent, options, executor, lockDirectory));
        return new CodeGraphOutcome(checks, BinaryMissing: false);
    }

    /// <summary>
    ///     Detects the binary by running its version probe. A start failure means the file is not on PATH, which is
    ///     the case that needs the user's approval; any other failure is a broken binary, not a missing one.
    /// </summary>
    private static bool DetectBinary(
        DoctorOptions options,
        IProcessExecutor executor,
        out bool missing,
        out string? reason)
    {
        missing = false;
        reason = null;
        try
        {
            ProcessResult result = executor.Run(StartInfo(options.CodeGraphExecutable, null, "--version"), string.Empty);
            if (result.ExitCode == 0)
                return true;

            // The file exists and runs, but cannot report its version. That is a broken binary, not an absent one:
            // installing it is not the remedy, so this is an ordinary failure and not the exit-3 approval path.
            string detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
            reason = $"'{options.CodeGraphExecutable} --version' exited {result.ExitCode}: {detail}";
            return false;
        }
        catch (Win32Exception)
        {
            missing = true;
            reason = $"'{options.CodeGraphExecutable}' is not on PATH";
            return false;
        }
        catch (TimeoutException)
        {
            reason = $"'{options.CodeGraphExecutable} --version' did not finish";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Starting a process can fail for reasons other than "not found", and the doctor reports rather than
            // throws. Only the Win32Exception above means the user has to decide whether to install it.
            reason = $"'{options.CodeGraphExecutable}' could not be run to report its version";
            return false;
        }
    }

    private static DoctorCheck CheckIndex(string indexDirectory, bool present)
    {
        if (!present)
            return new DoctorCheck(
                "codegraph.index",
                false,
                "no CodeGraph index at .codegraph/",
                "kyber-weave doctor --provision");

        if (!IsWritable(indexDirectory))
            return Unwritable();

        return new DoctorCheck("codegraph.index", true, "index present at .codegraph/", null);
    }

    private static DoctorCheck Provision(
        string root,
        string indexDirectory,
        bool present,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory)
    {
        using FileStream? lockFile = AcquireLock(
            LockPathFor(root, lockDirectory), options.IndexTimeout, out string? lockFailure);
        if (lockFile is null)
        {
            // Contention and an unusable lock path are different problems with different remedies: one is a wait,
            // the other is a filesystem the user has to look at. Neither may escape as an access exception.
            return lockFailure is { } failure
                ? new DoctorCheck("codegraph.index", false, failure, "kyber-weave doctor --provision")
                : new DoctorCheck(
                    "codegraph.index",
                    false,
                    "codegraph.index busy: another doctor is provisioning this repository",
                    "wait for the other kyber-weave doctor run to finish, then re-run: kyber-weave doctor --provision");
        }

        if (present && !IsWritable(indexDirectory))
            return Unwritable();

        // Verb choice follows the directory's presence as it stands now, not as it stood before the lock:
        // a concurrent run may have created the index while this one waited, and re-running init over an
        // existing directory would be the wrong verb. Both run from the root so the index lands there.
        string verb = Directory.Exists(indexDirectory) ? "index" : "init";
        ProcessStartInfo startInfo = StartInfo(options.CodeGraphExecutable, root, verb, ".");

        try
        {
            ProcessResult result = executor.Run(startInfo, string.Empty);
            if (result.ExitCode == 0)
                return new DoctorCheck("codegraph.index", true, $"codegraph {verb} completed", null);

            string detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
            return IsCorrupt(result)
                ? new DoctorCheck(
                    "codegraph.index",
                    false,
                    $"codegraph {verb} reports a damaged index: {detail}",
                    "remove .codegraph and run kyber-weave doctor")
                : new DoctorCheck(
                    "codegraph.index",
                    false,
                    $"codegraph {verb} exited {result.ExitCode}: {detail}",
                    $"codegraph {verb} .");
        }
        catch (TimeoutException)
        {
            return new DoctorCheck(
                "codegraph.index",
                false,
                $"codegraph {verb} exceeded its {options.IndexTimeout.TotalSeconds:0} second limit and was stopped",
                $"kyber-weave doctor --provision --index-timeout {Math.Max(1, (int)options.IndexTimeout.TotalSeconds) * 2}");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The binary resolved a moment ago in DetectBinary, so a start failure here is a race with the
            // environment rather than a missing prerequisite. Report it as a failed check.
            return new DoctorCheck(
                "codegraph.index",
                false,
                $"codegraph {verb} could not be run",
                $"codegraph {verb} .");
        }
    }

    private static DoctorCheck Unwritable() => new(
        "codegraph.index",
        false,
        ".codegraph/ is not writable or is owned by another user",
        "remove .codegraph and run kyber-weave doctor");

    /// <summary>
    ///     Takes the lock by opening the file with no sharing, retrying until the deadline. The file is never
    ///     removed here: removing a lock another run holds would let a third run in beside it.
    /// </summary>
    /// <returns>
    ///     The held stream, or <c>null</c>. <paramref name="failure"/> is null when another run holds the lock, and
    ///     names the reason when the lock path itself cannot be used.
    /// </returns>
    private static FileStream? AcquireLock(string path, TimeSpan wait, out string? failure)
    {
        failure = null;
        DateTime deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (DirectoryNotFoundException)
            {
                // A missing lock directory is not contention, and retrying it until the deadline would only delay
                // the same answer.
                failure = $"the index lock directory for '{path}' does not exist";
                return null;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockPollInterval);
            }
            catch (IOException)
            {
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failure = $"the index lock '{path}' could not be opened";
                return null;
            }
        }
    }

    /// <summary>Proves writability by creating and removing a probe file, which is the only file this code ever removes.</summary>
    private static bool IsWritable(string directory)
    {
        string probe = Path.Combine(directory, $".doctor-write-{Guid.NewGuid():N}.probe");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCorrupt(ProcessResult result)
    {
        // Only a recognised index-corruption diagnostic prescribes removing .codegraph/. A bare "corrupt"
        // or "malformed" anywhere in the output is not enough: a healthy index must never be deleted on the
        // strength of a substring in an unrelated error message.
        string text = result.StandardError + "\n" + result.StandardOutput;
        return text.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
               || text.Contains("malformed database", StringComparison.OrdinalIgnoreCase)
               || (text.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("index", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first non-empty line, trimmed and truncated, so a child's output cannot flood a failure line.</summary>
    private static string? FirstLine(string text)
    {
        string? line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
        return line is null ? null : line.Length > 160 ? line[..160] + "..." : line;
    }

    private static ProcessStartInfo StartInfo(string fileName, string? workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string Normalise(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
