using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     Checks and, when the mode allows, prepares the repository's CodeGraph index.
/// </summary>
/// <remarks>
///     <para>
///         Preparation runs <c>codegraph init .</c> when <c>.codegraph/</c> is absent and <c>codegraph sync .</c>
///         otherwise, with the repository root as the working directory. <c>sync</c> is the verb for a live index:
///         <c>init</c> over one throws away work that every later run then pays to rebuild. <c>index</c> is the repair
///         verb, and only the mode that repairs runs it. The root authorises a repository-local init. Nothing is
///         installed, and nothing is ever deleted: a damaged index is reported with a remedy a human runs, because
///         deleting a large index silently would throw that work away.
///     </para>
///     <para>
///         A cross-process lock file, keyed by a hash of the root, serialises concurrent preparation runs. A second
///         run waits for as long as its caller asked and then reports the index busy, rather than racing the first
///         run's writes.
///     </para>
/// </remarks>
public static class CodeGraphProvisioner
{
    /// <summary>The directory name CodeGraph keeps its index in, at the repository root.</summary>
    public const string IndexDirectoryName = ".codegraph";

    /// <summary>The check id the index preparation reports under. Agents branch on it.</summary>
    private const string IndexCheckId = "codegraph.index";

    /// <summary>The check id the restore of the index's own tracked writes reports under.</summary>
    private const string RestoreCheckId = "codegraph.restore";

    /// <summary>The check id the explore proof reports under.</summary>
    private const string ExploreCheckId = "codegraph.explore";

    /// <summary>Creates the index. The right verb only when there is none.</summary>
    private const string InitVerb = "init";

    /// <summary>Brings an existing index up to date. The right verb whenever <c>.codegraph/</c> exists.</summary>
    private const string SyncVerb = "sync";

    /// <summary>Rebuilds the index from scratch. The repair step, never the routine one.</summary>
    private const string IndexVerb = "index";

    /// <summary>Answers a structural question, which is how the ensure run proves the index is readable.</summary>
    private const string ExploreVerb = "explore";

    /// <summary>
    ///     The query the ensure run explores.
    /// </summary>
    /// <remarks>
    ///     Any question proves the same thing — that the index answers at all — and this one is also what the docs
    ///     probe asks, so both halves of the tool test exercise the same shape of call.
    /// </remarks>
    private const string ExploreQuery = "README";

    /// <summary>The <c>doctor</c> option a provision remedy names.</summary>
    private const string ProvisionFlag = "provision";

    /// <summary>The <c>doctor</c> option an ensure remedy names.</summary>
    private const string EnsureFlag = "ensure";

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

    /// <summary>Runs the CodeGraph checks, preparing the index in every mode except <see cref="DoctorMode.Check"/>.</summary>
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

        if (mode == DoctorMode.Check)
        {
            checks.Add(CheckIndex(indexDirectory, Directory.Exists(indexDirectory)));
            return new CodeGraphOutcome(checks, BinaryMissing: false);
        }

        if (mode == DoctorMode.Ensure)
        {
            checks.AddRange(Ensure(root, indexDirectory, options, executor, lockDirectory));
            return new CodeGraphOutcome(checks, BinaryMissing: false);
        }

        checks.Add(Provision(root, options, executor, lockDirectory, options.IndexTimeout));
        return new CodeGraphOutcome(checks, BinaryMissing: false);
    }

    /// <summary>
    ///     Brings the index up to date under the cross-process lock: <c>codegraph init</c> when <c>.codegraph/</c> is
    ///     absent and <c>codegraph sync</c> otherwise.
    /// </summary>
    /// <remarks>
    ///     Exposed so that a caller which needs the index refreshed can take the same lock and run the same verbs,
    ///     rather than writing a second, subtly different preparation path.
    /// </remarks>
    /// <param name="root">The repository root, which is also the child's working directory.</param>
    /// <param name="options">The CodeGraph executable and index budget.</param>
    /// <param name="executor">Runs the CodeGraph child.</param>
    /// <param name="lockDirectory">Where the lock file lives.</param>
    /// <param name="lockWait">
    ///     How long to wait for a lock another run holds. <see cref="TimeSpan.Zero"/> tries once and reports the index
    ///     busy instead of waiting, which is what a caller inside a tool call needs.
    /// </param>
    /// <returns>The <c>codegraph.index</c> check.</returns>
    public static DoctorCheck Provision(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        TimeSpan lockWait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        using FileStream? lockFile = AcquireLock(LockPathFor(root, lockDirectory), lockWait, out string? lockFailure);
        if (lockFile is null)
            return LockRefused(lockFailure, ProvisionFlag);

        string indexDirectory = Path.Combine(root, IndexDirectoryName);
        if (Directory.Exists(indexDirectory) && !IsWritable(indexDirectory))
            return Unwritable();

        // Verb choice follows the directory's presence as it stands now, not as it stood before the lock:
        // a concurrent run may have created the index while this one waited, and re-running init over an
        // existing directory would be the wrong verb. Both run from the root so the index lands there.
        string verb = Directory.Exists(indexDirectory) ? SyncVerb : InitVerb;
        return VerbCheck(RunChild(root, verb, options, executor, subject: "."), options, IndexCheckId, ProvisionFlag);
    }

    /// <summary>
    ///     Leaves the index usable, which is the index half of <c>doctor --ensure</c>: refresh or create it, undo any
    ///     tracked edit the preparation wrote, then prove it answers with one <c>codegraph explore</c>.
    /// </summary>
    /// <remarks>
    ///     A failed step is repaired once with <c>codegraph index</c> and retried once. One repair is enough for the
    ///     transient index damage this exists for; a loop would turn a genuinely broken CodeGraph into a slow hang
    ///     instead of a stop, and no retry at all would give up on an index left stale by an interrupted run.
    /// </remarks>
    private static List<DoctorCheck> Ensure(
        string root,
        string indexDirectory,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory)
    {
        List<DoctorCheck> checks = [];

        using FileStream? lockFile = AcquireLock(
            LockPathFor(root, lockDirectory), options.IndexTimeout, out string? lockFailure);
        if (lockFile is null)
        {
            checks.Add(LockRefused(lockFailure, EnsureFlag));
            return checks;
        }

        // The tracked files already modified before this run, sampled after acquiring the lock so
        // that operator edits made while waiting on another run are never rewound.
        HashSet<string>? dirtyBefore = TrackedChanges(root, executor);

        if (Directory.Exists(indexDirectory) && !IsWritable(indexDirectory))
        {
            checks.Add(Unwritable());
            return checks;
        }

        // Verb choice follows the directory as it stands inside the lock, for the same reason Provision re-derives it.
        string verb = Directory.Exists(indexDirectory) ? SyncVerb : InitVerb;
        ChildRun prepared = RunChild(root, verb, options, executor, subject: ".");
        if (!prepared.Ok)
        {
            ChildRun repair = RunChild(root, IndexVerb, options, executor, subject: ".");
            prepared = repair.Ok
                ? RunChild(
                    root,
                    Directory.Exists(indexDirectory) ? SyncVerb : InitVerb,
                    options,
                    executor,
                    subject: ".")
                : repair;
        }

        checks.Add(VerbCheck(prepared, options, IndexCheckId, EnsureFlag));

        if (RestoreWritten(root, dirtyBefore, executor) is { } restore)
            checks.Add(restore);

        // A repair already ran, and one is the whole budget. Proving an index that could not be prepared would report
        // the same breakage twice and pay a second child run for it.
        if (!prepared.Ok)
            return checks;

        // D22: one explore call, not a retrieval loop. It proves the index answers; a second query would make every
        // worker start pay for another index read, which is the cost this command exists to reduce.
        ChildRun proof = RunChild(root, ExploreVerb, options, executor, subject: ExploreQuery);
        if (!proof.Ok)
        {
            ChildRun repair = RunChild(root, IndexVerb, options, executor, subject: ".");
            proof = repair.Ok
                ? RunChild(root, ExploreVerb, options, executor, subject: ExploreQuery)
                : repair;
        }

        checks.Add(VerbCheck(proof, options, ExploreCheckId, EnsureFlag));
        return checks;
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
                IndexCheckId,
                false,
                "no CodeGraph index at .codegraph/",
                "kyber-weave doctor --provision");

        if (!IsWritable(indexDirectory))
            return Unwritable();

        return new DoctorCheck(IndexCheckId, true, "index present at .codegraph/", null);
    }

    /// <summary>
    ///     Restores the tracked files that appeared while the index was being prepared, and touches nothing that was
    ///     already modified.
    /// </summary>
    /// <returns>
    ///     The <c>codegraph.restore</c> check, or null when the preparation wrote no tracked file or when baseline
    ///     tracked changes could not be read.
    /// </returns>
    private static DoctorCheck? RestoreWritten(
        string root,
        HashSet<string>? dirtyBefore,
        IProcessExecutor executor)
    {
        if (dirtyBefore is null)
            return null;

        HashSet<string>? dirtyAfter = TrackedChanges(root, executor);
        if (dirtyAfter is null)
            return null;

        List<string> written = [];
        foreach (string path in dirtyAfter)
        {
            if (!dirtyBefore.Contains(path))
                written.Add(path);
        }

        if (written.Count == 0)
            return null;

        written.Sort(StringComparer.Ordinal);

        List<string> failed = [];
        foreach (string path in written)
        {
            if (!RestoreOne(root, path, executor))
                failed.Add(path);
        }

        return failed.Count == 0
            ? new DoctorCheck(
                RestoreCheckId,
                true,
                $"restored the tracked files the index wrote: {string.Join(", ", written)}",
                null)
            : new DoctorCheck(
                RestoreCheckId,
                false,
                $"the index wrote tracked files that could not be restored: {string.Join(", ", failed)}",
                $"git restore --source=HEAD --staged --worktree -- {string.Join(' ', failed.Select(QuoteShellPath))}");
    }

    private static string QuoteShellPath(string path) =>
        "'" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>Restores one path from <c>HEAD</c>, in both the working tree and the index.</summary>
    private static bool RestoreOne(string root, string path, IProcessExecutor executor)
    {
        try
        {
            ProcessResult result = executor.Run(
                StartInfo("git", root, "restore", "--source=HEAD", "--staged", "--worktree", "--", path),
                string.Empty);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The repository-relative paths git reports as modified, staged, deleted or renamed, from
    ///     <c>git status --porcelain -uno</c>, or null when git status fails.
    /// </summary>
    /// <remarks>
    ///     Untracked entries are excluded by <c>-uno</c>. This set exists so the ensure run can tell the edits it
    ///     caused from the edits that were already there, and a path git does not track cannot be restored. A git
    ///     that cannot run yields null rather than an empty set: failing to read the working tree must not be
    ///     confused with a clean tree, which would cause existing modifications to be restored as index-written.
    /// </remarks>
    private static HashSet<string>? TrackedChanges(string root, IProcessExecutor executor)
    {
        try
        {
            ProcessResult result = executor.Run(
                StartInfo("git", root, "status", "--porcelain", "-uno"),
                string.Empty);
            if (result.ExitCode != 0)
                return null;

            HashSet<string> paths = new(StringComparer.Ordinal);
            foreach (string line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                // Porcelain v1 is two status columns, a space, then the path. Anything shorter than that is not an
                // entry this code can name, and naming a fragment would restore a path that does not exist.
                if (line.Length <= 3)
                    continue;

                string path = line[3..].Trim().Trim('"');

                // A rename reports both paths; the one to restore is the one that exists now.
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                    path = path[(arrow + 4)..].Trim().Trim('"');

                if (path.Length > 0)
                    paths.Add(path);
            }

            return paths;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Runs one CodeGraph verb from the root and records what the check line needs to know about it.</summary>
    /// <param name="root">The repository root, which is the child's working directory.</param>
    /// <param name="verb">The CodeGraph verb.</param>
    /// <param name="options">The CodeGraph executable and index budget.</param>
    /// <param name="executor">Runs the child.</param>
    /// <param name="subject">The verb's argument: the path to prepare, or the question to explore.</param>
    private static ChildRun RunChild(
        string root,
        string verb,
        DoctorOptions options,
        IProcessExecutor executor,
        string subject)
    {
        try
        {
            ProcessResult result = executor.Run(
                StartInfo(options.CodeGraphExecutable, root, verb, subject),
                string.Empty);
            if (result.ExitCode == 0)
                return new ChildRun(verb, subject, ChildRunFailure.None, 0, null, Corrupt: false);

            string detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
            return new ChildRun(verb, subject, ChildRunFailure.Exited, result.ExitCode, detail, IsCorrupt(result));
        }
        catch (TimeoutException)
        {
            return new ChildRun(verb, subject, ChildRunFailure.TimedOut, 0, null, Corrupt: false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The binary resolved a moment ago in DetectBinary, so a start failure here is a race with the
            // environment rather than a missing prerequisite. Report it as a failed check.
            return new ChildRun(verb, subject, ChildRunFailure.CouldNotStart, 0, null, Corrupt: false);
        }
    }

    /// <summary>Turns one child run into the check line for <paramref name="checkId"/>, with a remedy that runs.</summary>
    private static DoctorCheck VerbCheck(ChildRun run, DoctorOptions options, string checkId, string flag)
    {
        if (run.Ok)
            return new DoctorCheck(checkId, true, $"codegraph {run.Verb} completed", null);

        // The exact argv the remedy prints is the argv that just failed, so a human can re-run it, and the
        // timeout remedy names the doubled budget as a whole second rather than the caller's raw value.
        string command = $"codegraph {run.Verb} {run.Subject}";

        return run.Failure switch
        {
            ChildRunFailure.TimedOut => new DoctorCheck(
                checkId,
                false,
                $"codegraph {run.Verb} exceeded its {options.IndexTimeout.TotalSeconds:0} second limit and was stopped",
                $"kyber-weave doctor --{flag} --index-timeout {RaisedTimeout(options)}"),
            ChildRunFailure.CouldNotStart => new DoctorCheck(checkId, false, $"{command} could not be run", command),
            _ => run.Corrupt
                ? new DoctorCheck(
                    checkId,
                    false,
                    $"codegraph {run.Verb} reports a damaged index: {run.Detail}",
                    "remove .codegraph and run kyber-weave doctor")
                : new DoctorCheck(
                    checkId,
                    false,
                    $"codegraph {run.Verb} exited {run.ExitCode}: {run.Detail}",
                    command)
        };
    }

    /// <summary>The timeout the remedy suggests: doubled, in <see cref="long"/> so a large budget cannot wrap.</summary>
    private static long RaisedTimeout(DoctorOptions options)
    {
        long seconds = Math.Max(1L, (long)Math.Ceiling(options.IndexTimeout.TotalSeconds));
        return Math.Min(seconds * 2L, int.MaxValue);
    }

    /// <summary>Whether a doctor check reports that the CodeGraph index was busy provisioning.</summary>
    public static bool IsBusy(DoctorCheck check) =>
        check.Reason?.StartsWith("codegraph.index busy", StringComparison.Ordinal) == true;

    /// <summary>
    ///     Whether an <see cref="IOException"/> represents a sharing or locking violation caused by another process.
    /// </summary>
    public static bool IsSharingViolation(IOException ex)
    {
        int hr = ex.HResult;
        if (hr is unchecked((int)0x80070020) or unchecked((int)0x80070021) || (hr & 0xFFFF) is 32 or 33)
            return true;

        string msg = ex.Message;
        return msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("sharing violation", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The check a run reports when another doctor holds the lock, or when the lock path is unusable.</summary>
    private static DoctorCheck LockRefused(string? failure, string flag) =>
        failure is { } reason
            ? new DoctorCheck(IndexCheckId, false, reason, $"kyber-weave doctor --{flag}")
            : new DoctorCheck(
                IndexCheckId,
                false,
                "codegraph.index busy: another doctor is provisioning this repository",
                $"wait for the other kyber-weave doctor run to finish, then re-run: kyber-weave doctor --{flag}");

    private static DoctorCheck Unwritable() => new(
        IndexCheckId,
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
            catch (PathTooLongException)
            {
                failure = $"the index lock path '{path}' is too long";
                return null;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockPollInterval);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return null;
            }
            catch (IOException ex)
            {
                failure = $"the index lock '{path}' could not be opened: {ex.Message}";
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

    /// <summary>What one CodeGraph child run leaves behind, so a check line can be built from it.</summary>
    /// <param name="Verb">The verb that ran.</param>
    /// <param name="Subject">The verb's argument, which the remedy reuses verbatim.</param>
    /// <param name="Failure">Why the run did not succeed.</param>
    /// <param name="ExitCode">The child's exit code, meaningful only when <paramref name="Failure"/> is <see cref="ChildRunFailure.Exited"/>.</param>
    /// <param name="Detail">The child's first output line, truncated.</param>
    /// <param name="Corrupt">Whether the output is a recognised index-corruption diagnostic.</param>
    private readonly record struct ChildRun(
        string Verb,
        string Subject,
        ChildRunFailure Failure,
        int ExitCode,
        string? Detail,
        bool Corrupt)
    {
        /// <summary>Whether the child exited zero.</summary>
        public bool Ok => Failure is ChildRunFailure.None;
    }

    /// <summary>Why a CodeGraph child run did not succeed.</summary>
    private enum ChildRunFailure
    {
        /// <summary>The child exited zero.</summary>
        None,

        /// <summary>The child ran and exited non-zero.</summary>
        Exited,

        /// <summary>The child exceeded the index budget and was stopped.</summary>
        TimedOut,

        /// <summary>The child could not be started.</summary>
        CouldNotStart
    }
}
