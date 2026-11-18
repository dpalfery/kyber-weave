using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Testing.Update;

/// <summary>
/// Stands in for the installed kyber-weave that an update hands off to after its binaries are committed.
/// </summary>
/// <remarks>
/// The hand-off starts <c>&lt;install dir&gt;/kyber-weave update &lt;version&gt; --finish-update</c>. In a test that
/// child is a second <see cref="SelfUpdater"/> built over the same handler, install directory and fakes, with the
/// host reporting the requested version, so the finish-mode work runs in-process and sees the same recorded calls
/// a real child would make. The rehearsal call (<c>--version</c>) is answered with 0 and runs nothing.
/// </remarks>
internal sealed class LoopbackFinishingProcess
{
    private readonly UpdateWorld _world;
    private readonly HttpMessageHandler _handler;
    private readonly SelfUpdateHost _parentHost;
    private readonly Action<string> _log;
    private readonly Func<string, string?>? _readEnvironment;
    private readonly Func<string, IReadOnlyList<string>, int>? _runProcess;
    private readonly Func<string, bool>? _canWriteDirectory;
    private readonly Func<string, IReadOnlyList<string>, ProcessResult>? _captureProcess;
    private readonly IArbiterHookRefresher? _arbiterHookRefresher;
    private readonly IArbiterRefreshTargets? _arbiterTargets;

    /// <summary>
    /// The outcome the finish-mode child returned on its last hand-off, with its own message. The parent only receives
    /// the exit code through the seam, because the real child prints its message on the inherited console; this keeps
    /// the child's text reachable to a test without a static.
    /// </summary>
    internal SelfUpdateOutcome? LastChildOutcome { get; private set; }

    internal LoopbackFinishingProcess(
        UpdateWorld world,
        HttpMessageHandler handler,
        SelfUpdateHost parentHost,
        Action<string> log,
        Func<string, string?>? readEnvironment = null,
        Func<string, IReadOnlyList<string>, int>? runProcess = null,
        Func<string, bool>? canWriteDirectory = null,
        Func<string, IReadOnlyList<string>, ProcessResult>? captureProcess = null,
        IArbiterHookRefresher? arbiterHookRefresher = null,
        IArbiterRefreshTargets? arbiterTargets = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(parentHost);
        ArgumentNullException.ThrowIfNull(log);
        _world = world;
        _handler = handler;
        _parentHost = parentHost;
        _log = log;
        _readEnvironment = readEnvironment;
        _runProcess = runProcess;
        _canWriteDirectory = canWriteDirectory;
        _captureProcess = captureProcess;
        _arbiterHookRefresher = arbiterHookRefresher;
        _arbiterTargets = arbiterTargets;
    }

    /// <summary>The seam value: runs the hand-off's process and returns its exit code.</summary>
    internal int Invoke(string fileName, IReadOnlyList<string> arguments, bool discardOutput)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 1 && arguments[0] == "--version")
            return 0;

        if (arguments.Count < 3 || arguments[0] != "update" || arguments[2] != "--finish-update")
            throw new InvalidOperationException(
                "The loopback accepts only the rehearsal (--version) and `update <version> --finish-update ...`; got: "
                + string.Join(" ", arguments));

        string version = arguments[1];
        SelfUpdateHost child = _parentHost with
        {
            ProcessPath = fileName,
            CurrentVersion = version,
        };

        using SelfUpdater updater = new(
            _handler,
            child,
            _world.Files,
            _world.Paths,
            _world.Environment(_readEnvironment),
            _world.CreateSystem(),
            _log,
            runProcess: _runProcess,
            canWriteDirectory: _canWriteDirectory,
            captureProcess: _captureProcess,
            arbiterHookRefresher: _arbiterHookRefresher,
            finishingProcess: null,
            arbiterTargets: _arbiterTargets);
        SelfUpdateOutcome outcome = updater.Run(new SelfUpdateOptions(
            version,
            ReleaseCandidate: false,
            NoMcp: false,
            NoKyberDash: arguments.Contains("--no-kyberdash"),
            NoMenubar: arguments.Contains("--no-menubar"),
            NoArbiter: arguments.Contains("--no-arbiter"),
            NoRestart: arguments.Contains("--no-restart"),
            FinishUpdate: true));
        LastChildOutcome = outcome;
        return outcome.ExitCode;
    }
}
