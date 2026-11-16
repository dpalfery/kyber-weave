using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;

namespace KyberWeave.Mcp;

/// <summary>
/// Keeps the CodeGraph index the docs tools answer from as fresh as its callers need, and puts a
/// CodeGraph failure in front of them in the form they act on.
/// </summary>
/// <remarks>
/// <para>
/// The server used to leave the index to whoever ran <c>codegraph</c> last, so throughout a
/// session the code joins described a tree that had since moved. Refreshing on every call would
/// put a whole index behind every question instead. The cadence is therefore the first call that
/// needs it, then at most once per <see cref="RefreshInterval"/>.
/// </para>
/// <para>
/// The refresh is not a second way to prepare an index: it takes the same cross-process lock and
/// runs the same verbs as <c>kyber-weave doctor</c>, through
/// <see cref="ICodeGraphProvisioner.Provision"/>. Two things differ. The lock is taken with no
/// wait, because waiting would put another process's index on this call's critical path. And a
/// lock another process holds is reported as a note rather than a failure, because the index on
/// disk is then at worst one refresh behind, not broken.
/// </para>
/// <para>
/// CodeGraph keeps a lock of its own, a file its server, its file watcher and any other sync
/// create while they write. The refresh never starts a sync without first looking for that file.
/// While it exists the refresh waits and looks again, on the owner's schedule
/// (<see cref="CodeGraphIndexRules.LockFileWaits"/>), and after the last look it fails the tool
/// without syncing. That verdict is not kept for the refresh interval: the next call looks again.
/// </para>
/// <para>
/// A sync can still lose the race and find the lock taken after the look. That is contention on a
/// healthy index, so it gets a few short retries
/// (<see cref="CodeGraphIndexRules.HeldLockRetryPauses"/>), each after its own look, then a note,
/// never a failure. Every other sync failure still fails the tool.
/// </para>
/// <para>
/// The child's output is captured by <see cref="ProcessRunner"/> and is never read into a
/// response or a stream of ours: stdout carries the JSON-RPC transport, and one line written
/// there breaks the session.
/// </para>
/// <para>
/// The configuration is read again at each attempt, so a repository that changes
/// <c>stop-on-tool-failure</c> is answered by the new setting without a restart. Both the refresh
/// and the failure are gated on the repository declaring the code-graph server in
/// <c>mcp.required-servers</c> (<see cref="McpConfig.DeclaresCodeGraph"/>): a repository that
/// declares it is refreshed and stopped when the index cannot be read, and every other repository
/// is left answering from whatever is on disk, exactly as it did before this refresh existed.
/// </para>
/// </remarks>
public sealed class CodeGraphFreshness : ICodeGraphFreshnessGate
{
    /// <summary>How long one refresh may take before it is stopped and reported as failed.</summary>
    private static readonly TimeSpan RefreshBound = TimeSpan.FromSeconds(30);

    /// <summary>The shortest gap between two refresh attempts.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>The verb and bound the refresh is run as, with the doctor's own entry point named in the remedy.</summary>
    private const string CodeGraphExecutable = "codegraph";

    /// <summary>The remedy every CodeGraph failure ends with, whatever the provisioner suggested.</summary>
    private const string DoctorRemedy = "kyber-weave doctor --ensure";

    /// <summary>What a failure reports when the cause carried no text of its own.</summary>
    private const string UnknownReason = "CodeGraph could not be read";

    /// <summary>
    /// What a caller is told when another process holds the refresh lock. One line, and it says
    /// the index is being updated rather than that it is broken.
    /// </summary>
    private const string BusyNote =
        "note: codegraph refresh skipped — another process holds the index lock and is refreshing "
        + "it; this answer uses the index as it stands.";

    /// <summary>
    /// What a caller is told when every sync attempt found CodeGraph's own index lock held. It
    /// opens like <see cref="BusyNote"/> so one rule reads both.
    /// </summary>
    private const string IndexLockHeldNote =
        "note: codegraph refresh skipped — CodeGraph's own index lock is held by another process "
        + "(its server, its file watcher, or another sync); this answer uses the index as it stands.";

    private readonly string _repositoryRoot;
    private readonly bool _refreshEnabled;
    private readonly ICodeGraphProvisioner _provisioner;
    private readonly IKyberWeaveConfigRepository _config;
    private readonly TimeProvider _time;
    private readonly IProcessExecutor _executor;
    private readonly Action<TimeSpan> _pause;
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastAttempt;
    private CodeGraphGate _last = CodeGraphGate.Clear;

    /// <summary>Freshness checks for the index of <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="refreshEnabled">
    /// False when the process was started with <c>--no-codegraph-refresh</c>, which is how
    /// <c>doctor --check</c> keeps its promise that the server never writes an index.
    /// </param>
    /// <param name="provisioner">Brings the index up to date under the cross-process lock.</param>
    /// <param name="config">Reads the repository's declaration and its stop rule at each attempt.</param>
    /// <param name="time">Supplies the clock the refresh interval is measured against.</param>
    /// <param name="executor">
    /// Runs the CodeGraph child the provisioner starts, or null for one bounded by
    /// <see cref="RefreshBound"/>. The provisioner takes its executor from the caller for the same
    /// reason it takes the root: a caller that must answer without a CodeGraph binary — a test, or a
    /// host that supplies its own runner — supplies the executor rather than being answered by
    /// whichever binary this machine happens to have installed.
    /// </param>
    /// <param name="pause">
    /// Waits between two sync attempts when CodeGraph's own lock is held, or null to block the
    /// calling thread. A test supplies one so it does not wait in real time.
    /// </param>
    public CodeGraphFreshness(
        string repositoryRoot,
        bool refreshEnabled,
        ICodeGraphProvisioner provisioner,
        IKyberWeaveConfigRepository config,
        TimeProvider time,
        IProcessExecutor? executor = null,
        Action<TimeSpan>? pause = null)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _refreshEnabled = refreshEnabled;
        _provisioner = provisioner;
        _config = config;
        _time = time;
        _executor = executor ?? new BoundedExecutor(RefreshBound);
        _pause = pause ?? Thread.Sleep;
    }

    /// <summary>
    /// The gate's verdict for this call: no refresh was due, the refresh is someone else's, or
    /// CodeGraph failed in a way the caller has to be told about.
    /// </summary>
    /// <param name="resolverFactory">Builds the CodeGraph port the docs tools answer from.</param>
    public CodeGraphGate Evaluate(Func<ICodeGraphResolver> resolverFactory)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);

        if (!_refreshEnabled) return CodeGraphGate.Clear;

        // A refresh already under way in this process is answered the way one under way in
        // another process is: with the last verdict, rather than by waiting on this process's own
        // index work before the caller can have its answer.
        if (!_gate.TryEnter()) return _last;

        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_lastAttempt is { } last && now - last < RefreshInterval)
                return _last;

            try
            {
                _last = Refresh(resolverFactory, out bool lockFileRemained);

                // A lock file that outlasted the waits is not a verdict to keep: the next call looks again.
                _lastAttempt = lockFileRemained ? null : now;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                KyberWeaveConfigLoadResult loaded = _config.TryLoad(_repositoryRoot);
                bool stops = loaded is { Success: true, Config: not null } ? loaded.Config.StopsOnToolFailure : true;
                _last = Failure(stops, exception.Message, null);
                _lastAttempt = now;
            }

            return _last;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private CodeGraphGate Refresh(Func<ICodeGraphResolver> resolverFactory, out bool lockFileRemained)
    {
        lockFileRemained = false;
        KyberWeaveConfigLoadResult loaded = _config.TryLoad(_repositoryRoot);
        if (loaded is not { Success: true, Config: not null })
            return CodeGraphGate.Clear;

        // The refresh and the failure apply only where the repository declares CodeGraph. The
        // scaffolded declaration leaves the CodeGraph entry commented out, so a repository that
        // never uncommented it has claimed no dependency on an index: it keeps the behaviour it
        // had before this refresh existed — no index lock, no TOOL_FAILURE, no warning line — and
        // other repositories keep working.
        if (!loaded.Config.Mcp.DeclaresCodeGraph)
            return CodeGraphGate.Clear;

        bool stops = loaded.Config.StopsOnToolFailure;
        string lockDirectory = Path.GetTempPath();

        if (!WaitForNoIndexLockFile())
        {
            lockFileRemained = true;
            return IndexLockFileRemained(stops);
        }

        DoctorOptions options = new(RefreshBound, RefreshBound, CodeGraphExecutable, "kyber-weave-mcp");
        DoctorCheck provisioned = ProvisionPastAHeldIndexLock(options, lockDirectory);

        if (!provisioned.Passed)
        {
            if (CodeGraphIndexRules.IsBusy(provisioned))
                return CodeGraphGate.Advisory(BusyNote);

            return CodeGraphIndexRules.IsIndexLockHeld(provisioned)
                ? CodeGraphGate.Advisory(IndexLockHeldNote)
                : Failure(stops, provisioned.Reason, provisioned.Fix);
        }

        ICodeGraphResolver resolver = resolverFactory();
        return resolver.IsAvailable
            ? CodeGraphGate.Clear
            : Failure(stops, resolver.UnavailableReason, null);
    }

    /// <summary>
    /// Looks for CodeGraph's lock file, and while it exists waits and looks again after each of
    /// <see cref="CodeGraphIndexRules.LockFileWaits"/>. The file's existence is the test, not
    /// whether the process it names is running.
    /// </summary>
    /// <returns>True as soon as a look finds no lock file; false when the last look still finds it.</returns>
    private bool WaitForNoIndexLockFile()
    {
        string lockFile = CodeGraphIndexRules.IndexLockPathFor(_repositoryRoot);
        foreach (TimeSpan wait in CodeGraphIndexRules.LockFileWaits)
        {
            if (!File.Exists(lockFile))
                return true;

            _pause(wait);
        }

        return !File.Exists(lockFile);
    }

    /// <summary>
    /// The failure for a lock file that outlasted every wait: how long was waited, the file, the
    /// process it names and whether that process is running, and the repair that follows from it.
    /// </summary>
    private CodeGraphGate IndexLockFileRemained(bool stops)
    {
        string lockFile = CodeGraphIndexRules.IndexLockPathFor(_repositoryRoot);
        double waited = CodeGraphIndexRules.LockFileWaits.Sum(wait => wait.TotalSeconds);
        int? holder = ReadLockHolder(lockFile);
        bool running = holder is { } processId && IsRunning(processId);

        string owner = holder is { } id
            ? $"it records PID {id.ToString(CultureInfo.InvariantCulture)}, which is {(running ? "running" : "not running")}"
            : "it records no PID";
        string fix = running
            ? $"wait for process {holder!.Value.ToString(CultureInfo.InvariantCulture)} to finish its index work, or stop it"
            : $"codegraph unlock {CodeGraphIndexRules.QuoteShellPath(_repositoryRoot, OperatingSystem.IsWindows())}";

        return Failure(
            stops,
            $"the CodeGraph index lock was still held after {waited.ToString("0", CultureInfo.InvariantCulture)} seconds: "
            + $"{lockFile} exists and {owner}.",
            fix);
    }

    /// <summary>The process id the lock file records, or null when the file is gone, unreadable, or holds no number.</summary>
    private static int? ReadLockHolder(string lockFile)
    {
        try
        {
            return int.TryParse(File.ReadAllText(lockFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
                ? processId
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Provisions once, and again after each of <see cref="DoctorOptions.HeldLockRetryPauses"/> while the only
    /// thing wrong is that CodeGraph's own index lock is held. Any other result is returned at once.
    /// A retry looks for the lock file first, like every sync, and does not sync while it exists.
    /// </summary>
    private DoctorCheck ProvisionPastAHeldIndexLock(DoctorOptions options, string lockDirectory)
    {
        string lockFile = CodeGraphIndexRules.IndexLockPathFor(_repositoryRoot);
        DoctorCheck provisioned = Provision(options, lockDirectory);
        foreach (TimeSpan pause in options.HeldLockRetryPauses)
        {
            if (!CodeGraphIndexRules.IsIndexLockHeld(provisioned))
                break;

            _pause(pause);
            if (!File.Exists(lockFile))
                provisioned = Provision(options, lockDirectory);
        }

        return provisioned;
    }

    private DoctorCheck Provision(DoctorOptions options, string lockDirectory) =>
        _provisioner.Provision(_repositoryRoot, options, _executor, lockDirectory, TimeSpan.Zero);

    /// <summary>
    /// The failure a tool reports under <paramref name="stops"/>, and the warning it carries
    /// instead when the repository set <c>stop-on-tool-failure: false</c>.
    /// </summary>
    /// <remarks>
    /// Both forms name the failing tool and the repair. Under the stop rule the response is the
    /// failure and nothing else, because a caller reading an answer cannot tell a degraded one
    /// from a complete one. The token is a whole line so a passing mention of it cannot satisfy
    /// an orchestrator that routes on it.
    /// </remarks>
    private static CodeGraphGate Failure(bool stops, string? reason, string? fix)
    {
        string detail = string.IsNullOrWhiteSpace(reason)
            ? UnknownReason
            : reason.ReplaceLineEndings(" ");
        string remedy = string.IsNullOrWhiteSpace(fix) ? DoctorRemedy : fix.ReplaceLineEndings(" ");

        if (!stops)
        {
            return CodeGraphGate.Advisory(
                "warning: codegraph is unavailable: " + detail
                + " Fix: " + Remedies(remedy)
                + " The answer below comes from the documents on disk, so code joins may be missing.");
        }

        return CodeGraphGate.Blocking(
            "STATUS: TOOL_FAILURE" + Environment.NewLine
            + "tool: codegraph" + Environment.NewLine
            + "reason: " + detail + Environment.NewLine
            + "fix: " + Remedies(remedy));
    }

    /// <summary>The repair, with the doctor named even when the provisioner's own suggestion is a bare CodeGraph verb.</summary>
    private static string Remedies(string fix) =>
        fix.Contains("kyber-weave doctor", StringComparison.Ordinal)
            ? fix
            : fix + "; or run: " + DoctorRemedy;

    /// <summary>
    /// Runs one CodeGraph child, bounded by the refresh budget, through the repository's
    /// deadlock-safe runner. The provisioner takes a process executor so a caller can bound and
    /// fake it; the production implementation the CLI uses is the CLI's, so this server carries
    /// the same three-line shape rather than a second way to start a process.
    /// </summary>
    private sealed class BoundedExecutor(TimeSpan timeout) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
            ProcessRunner.Run(startInfo, standardInput, timeout);
    }
}
