using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Doctor;
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
/// <see cref="CodeGraphProvisioner.Provision"/>. Two things differ. The lock is taken with no
/// wait, because waiting would put another process's index on this call's critical path. And a
/// lock another process holds is reported as a note rather than a failure, because the index on
/// disk is then at worst one refresh behind, not broken.
/// </para>
/// <para>
/// The child's output is captured by <see cref="ProcessRunner"/> and is never read into a
/// response or a stream of ours: stdout carries the JSON-RPC transport, and one line written
/// there breaks the session.
/// </para>
/// <para>
/// The configuration is read again at each attempt, so a repository that changes
/// <c>stop-on-tool-failure</c> is answered by the new setting without a restart. A repository
/// that declares no required MCP server has not asked this server to govern its tooling, and is
/// left answering from whatever is on disk, exactly as before.
/// </para>
/// </remarks>
public sealed class CodeGraphFreshness
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

    private readonly string _repositoryRoot;
    private readonly bool _refreshEnabled;
    private readonly Lock _gate = new();

    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private CodeGraphGate _last = CodeGraphGate.Clear;

    /// <summary>Freshness checks for the index of <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="refreshEnabled">
    /// False when the process was started with <c>--no-codegraph-refresh</c>, which is how
    /// <c>doctor --check</c> keeps its promise that the server never writes an index.
    /// </param>
    public CodeGraphFreshness(string repositoryRoot, bool refreshEnabled)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _refreshEnabled = refreshEnabled;
    }

    /// <summary>
    /// The gate's verdict for this call: no refresh was due, the refresh is someone else's, or
    /// CodeGraph failed in a way the caller has to be told about.
    /// </summary>
    /// <param name="resolverFactory">Builds the CodeGraph port the docs tools answer from.</param>
    internal CodeGraphGate Evaluate(Func<ICodeGraphResolver> resolverFactory)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);

        if (!_refreshEnabled) return CodeGraphGate.Clear;

        // A refresh already under way in this process is answered the way one under way in
        // another process is: with the last verdict, rather than by waiting on this process's own
        // index work before the caller can have its answer.
        if (!_gate.TryEnter()) return _last;

        try
        {
            DateTime now = DateTime.UtcNow;
            if (_lastAttemptUtc != DateTime.MinValue && now - _lastAttemptUtc < RefreshInterval)
                return _last;

            _lastAttemptUtc = now;
            _last = Refresh(resolverFactory);
            return _last;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private CodeGraphGate Refresh(Func<ICodeGraphResolver> resolverFactory)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(_repositoryRoot);
        if (loaded is not { Success: true, Config: not null })
            return CodeGraphGate.Clear;

        // A repository that requires no MCP server has declared no dependency on this one, so the
        // server keeps the behaviour it had before the refresh existed.
        if (loaded.Config.Mcp.RequiredServers.Count == 0)
            return CodeGraphGate.Clear;

        bool stops = loaded.Config.StopsOnToolFailure;
        string lockDirectory = Path.GetTempPath();
        string lockPath = CodeGraphProvisioner.LockPathFor(_repositoryRoot, lockDirectory);

        if (IsLocked(lockPath)) return CodeGraphGate.Advisory(BusyNote);

        DoctorOptions options = new(RefreshBound, RefreshBound, CodeGraphExecutable, "kyber-weave-mcp");
        DoctorCheck provisioned = CodeGraphProvisioner.Provision(
            _repositoryRoot,
            options,
            new BoundedExecutor(RefreshBound),
            lockDirectory,
            TimeSpan.Zero);

        if (!provisioned.Passed)
        {
            // A refresh that failed while another process holds the lock is still contention
            // rather than a broken index; the lock is the one honest witness of that.
            return IsLocked(lockPath)
                ? CodeGraphGate.Advisory(BusyNote)
                : Failure(stops, provisioned.Reason, provisioned.Fix);
        }

        ICodeGraphResolver resolver = resolverFactory();
        return resolver.IsAvailable
            ? CodeGraphGate.Clear
            : Failure(stops, resolver.UnavailableReason, null);
    }

    /// <summary>
    /// Whether another process holds the refresh lock, asked the way the provisioner asks it: by
    /// opening the file with no sharing. A lock file nothing holds is opened and released here.
    /// </summary>
    /// <remarks>
    /// A lock that cannot be opened for any other reason — a missing lock directory, a permission
    /// — is not contention, and is left for <see cref="CodeGraphProvisioner.Provision"/> to
    /// report with the reason it has. The directory case is caught first because .NET derives
    /// <see cref="DirectoryNotFoundException"/> from <see cref="IOException"/>.
    /// </remarks>
    private static bool IsLocked(string lockPath)
    {
        try
        {
            using FileStream probe = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
    }

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

/// <summary>What one CodeGraph check means for the tool call that asked for it.</summary>
/// <param name="Failure">The block a tool returns instead of its own answer, or null.</param>
/// <param name="Note">One line the answer carries above its body, or null.</param>
internal sealed record CodeGraphGate(string? Failure, string? Note)
{
    /// <summary>CodeGraph is usable, and the call proceeds unchanged.</summary>
    public static CodeGraphGate Clear { get; } = new(null, null);

    /// <summary>The call must report the failure instead of answering.</summary>
    public static CodeGraphGate Blocking(string failure) => new(failure, null);

    /// <summary>The call answers, and says what happened in one line first.</summary>
    public static CodeGraphGate Advisory(string note) => new(null, note);
}
