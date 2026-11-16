using System.Diagnostics;

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
/// CodeGraph keeps a lock of its own, which its server, its file watcher and any other sync take.
/// A sync that finds it held is the same condition one layer down, so it gets the same answer: a
/// few short retries (<see cref="HeldLockRetryPauses"/>), then a note, never a failure. Every other
/// sync failure still fails the tool.
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

    /// <summary>
    /// The pauses before the second and third sync when CodeGraph's own lock is held.
    /// </summary>
    /// <remarks>
    /// Measured against CodeGraph 1.6.2: a sync that finds the lock held returns in about 0.15
    /// seconds, and a sync with nothing to do holds the lock for about 0.3 seconds. Three attempts
    /// one second apart in total outlast that and the short syncs a file watcher runs, and cost a
    /// tool call at most about 1.5 seconds. A longer hold is an index rebuild, which no wait a tool
    /// call can afford would outlast.
    /// </remarks>
    private static readonly TimeSpan[] HeldLockRetryPauses =
        [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700)];

    private readonly string _repositoryRoot;
    private readonly bool _refreshEnabled;
    private readonly ICodeGraphProvisioner _provisioner;
    private readonly IKyberWeaveConfigRepository _config;
    private readonly TimeProvider _time;
    private readonly IProcessExecutor _executor;
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
    public CodeGraphFreshness(
        string repositoryRoot,
        bool refreshEnabled,
        ICodeGraphProvisioner provisioner,
        IKyberWeaveConfigRepository config,
        TimeProvider time,
        IProcessExecutor? executor = null)
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
                _last = Refresh(resolverFactory);
                _lastAttempt = now;
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

    private CodeGraphGate Refresh(Func<ICodeGraphResolver> resolverFactory)
    {
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
    /// Provisions once, and again after each of <see cref="HeldLockRetryPauses"/> while the only
    /// thing wrong is that CodeGraph's own index lock is held. Any other result is returned at once.
    /// </summary>
    private DoctorCheck ProvisionPastAHeldIndexLock(DoctorOptions options, string lockDirectory)
    {
        DoctorCheck provisioned = Provision(options, lockDirectory);
        foreach (TimeSpan pause in HeldLockRetryPauses)
        {
            if (!CodeGraphIndexRules.IsIndexLockHeld(provisioned))
                break;

            Thread.Sleep(pause);
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
