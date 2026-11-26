using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Processes;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Process coordination for the single <c>kyberdash-api</c> engine: checking it, replacing a live
/// engine running another version, acquiring the IPC lease, and publishing <c>server.json</c>
/// (<c>kyberdash engine ensure</c>, <c>kyberdash web</c>).
/// </summary>
/// <remarks>
/// Replacing an engine never leaves the operator with none. The live engine is asked to shut down,
/// the old process is waited out, the sibling binary is launched detached with <c>--no-open</c>, and
/// only when a new engine answers on the control socket is the replacement reported as succeeded.
/// Every launch goes through <see cref="ProcessRunner"/> so it stays bounded like every other spawn.
/// </remarks>
public class EngineLifecycleService : IEngineLifecycleService
{
    /// <summary>Sibling executable launched when a live engine has to be replaced.</summary>
    public const string EngineExecutableName = "kyberdash-api";

    /// <summary>Argument that stops a spawned engine from opening a browser window.</summary>
    public const string NoOpenArgument = "--no-open";

    /// <summary>Poll interval used while waiting for an engine to stop or start.</summary>
    public static TimeSpan PollInterval => TimeSpan.FromMilliseconds(150);

    /// <summary>Number of polls allowed while waiting for the old engine to exit (30 × 150 ms).</summary>
    public const int ShutdownPollAttempts = 30;

    /// <summary>Number of polls allowed while waiting for the replacement engine to answer (40 × 150 ms).</summary>
    public const int StartupPollAttempts = 40;

    private const string UnresponsiveMessage =
        "engine: unresponsive the engine endpoint is held by a process that does not answer; stop it and retry";

    private const string RefusedShutdownMessage = "engine: failed the live engine refused the shutdown request";

    private readonly IServerStateRepository serverState;
    private readonly DashOptions options;
    private readonly Func<string, string, bool> spawnEngine;

    /// <summary>
    /// Initializes a new <see cref="EngineLifecycleService"/>.
    /// </summary>
    /// <param name="serverState">Server-state repository owning the engine lease and descriptors.</param>
    /// <param name="options">Composition options naming the state directory and reported version.</param>
    /// <param name="spawnEngine">
    /// Starts the replacement engine from its binary path and state directory and reports whether it started;
    /// the detached <c>/bin/sh</c> launch when <see langword="null"/>. The seam lets a test see which binary is launched.
    /// </param>
    public EngineLifecycleService(IServerStateRepository serverState, DashOptions options, Func<string, string, bool>? spawnEngine = null)
    {
        ArgumentNullException.ThrowIfNull(serverState);
        ArgumentNullException.ThrowIfNull(options);

        this.serverState = serverState;
        this.options = options;
        this.spawnEngine = spawnEngine ?? TrySpawnDetachedEngine;
    }

    /// <inheritdoc />
    public string DefaultStateDirectory => serverState.DefaultStateDirectory;

    /// <inheritdoc />
    public async Task<EngineEnsureReport> EnsureRunningEngineMatchesVersionAsync(
        string expectedVersion,
        string? stateDir = null,
        string? apiBinaryPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);

        string resolvedStateDir = ResolveStateDirectory(stateDir);
        string version = string.IsNullOrWhiteSpace(expectedVersion) ? options.Version : expectedVersion.Trim();

        (EngineProbeState status, EngineDescriptor? holder) = await serverState
            .ProbeEngineAsync(resolvedStateDir, cancellationToken)
            .ConfigureAwait(false);

        if (status == EngineProbeState.NotRunning)
        {
            return new EngineEnsureReport(true, "engine: not-running");
        }

        if (status == EngineProbeState.Unresponsive || holder is null)
        {
            return new EngineEnsureReport(false, UnresponsiveMessage);
        }

        if (string.Equals(holder.Version, version, StringComparison.Ordinal))
        {
            return new EngineEnsureReport(true, $"engine: attached {version}", ActiveEngine: holder);
        }

        bool stopped = await serverState
            .RequestEngineShutdownAsync(resolvedStateDir, cancellationToken)
            .ConfigureAwait(false);
        if (!stopped)
        {
            return new EngineEnsureReport(false, RefusedShutdownMessage);
        }

        for (int attempt = 0; attempt < ShutdownPollAttempts; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            (EngineProbeState next, _) = await serverState
                .ProbeEngineAsync(resolvedStateDir, cancellationToken)
                .ConfigureAwait(false);
            if (next == EngineProbeState.NotRunning)
            {
                break;
            }
        }

        string exePath = ResolveApiBinary(apiBinaryPath);
        if (!spawnEngine(exePath, resolvedStateDir))
        {
            return new EngineEnsureReport(false, $"engine: failed could not start {exePath} {NoOpenArgument}");
        }

        for (int attempt = 0; attempt < StartupPollAttempts; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            (EngineProbeState restarted, EngineDescriptor? nextHolder) = await serverState
                .ProbeEngineAsync(resolvedStateDir, cancellationToken)
                .ConfigureAwait(false);
            if (restarted == EngineProbeState.Running && nextHolder is not null)
            {
                if (!string.Equals(nextHolder.Version, version, StringComparison.Ordinal))
                {
                    return new EngineEnsureReport(
                        false,
                        $"engine: failed replacement reports {nextHolder.Version}, expected {version}",
                        ActiveEngine: nextHolder);
                }

                return new EngineEnsureReport(
                    true,
                    $"engine: replaced {holder.Version} -> {nextHolder.Version}",
                    Replaced: true,
                    ActiveEngine: nextHolder);
            }
        }

        return new EngineEnsureReport(false, "engine: failed replacement engine did not publish ready status in time");
    }

    /// <inheritdoc />
    public async Task<EngineEnsureReport> StopRunningEngineAsync(
        string? stateDir = null,
        CancellationToken cancellationToken = default)
    {
        string resolvedStateDir = ResolveStateDirectory(stateDir);

        (EngineProbeState status, EngineDescriptor? holder) = await serverState
            .ProbeEngineAsync(resolvedStateDir, cancellationToken)
            .ConfigureAwait(false);

        if (status == EngineProbeState.NotRunning)
        {
            return new EngineEnsureReport(true, "engine: not-running");
        }

        if (status == EngineProbeState.Unresponsive || holder is null)
        {
            return new EngineEnsureReport(false, UnresponsiveMessage);
        }

        bool accepted = await serverState
            .RequestEngineShutdownAsync(resolvedStateDir, cancellationToken)
            .ConfigureAwait(false);
        if (!accepted)
        {
            return new EngineEnsureReport(false, RefusedShutdownMessage);
        }

        for (int attempt = 0; attempt < ShutdownPollAttempts; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            (EngineProbeState next, _) = await serverState
                .ProbeEngineAsync(resolvedStateDir, cancellationToken)
                .ConfigureAwait(false);
            if (next == EngineProbeState.NotRunning)
            {
                return new EngineEnsureReport(true, $"engine: stopped {holder.Version}");
            }
        }

        return new EngineEnsureReport(false, "engine: failed the engine did not stop in time");
    }

    /// <inheritdoc />
    public async Task<EngineEnsureReport> VerifyRunningEngineVersionAsync(
        string expectedVersion,
        int attempts,
        string? stateDir = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        string resolvedStateDir = ResolveStateDirectory(stateDir);
        string version = expectedVersion.Trim();
        EngineEnsureReport report = new(false, "engine: not-running", Outcome: EngineVerifyOutcome.NotRunning);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            (EngineProbeState status, EngineDescriptor? holder) = await serverState
                .ProbeEngineAsync(resolvedStateDir, cancellationToken)
                .ConfigureAwait(false);
            report = ClassifyEngine(status, holder, version);
            if (report.Outcome == EngineVerifyOutcome.Running)
            {
                break;
            }
        }

        return report;
    }

    private static EngineEnsureReport ClassifyEngine(EngineProbeState status, EngineDescriptor? holder, string version)
    {
        if (status == EngineProbeState.NotRunning)
        {
            return new EngineEnsureReport(false, "engine: not-running", Outcome: EngineVerifyOutcome.NotRunning);
        }

        if (status == EngineProbeState.Unresponsive || holder is null)
        {
            return new EngineEnsureReport(false, UnresponsiveMessage, Outcome: EngineVerifyOutcome.Unresponsive);
        }

        if (string.Equals(holder.Version, version, StringComparison.Ordinal))
        {
            return new EngineEnsureReport(
                true,
                $"engine: running {version} (pid {holder.Pid})",
                ActiveEngine: holder,
                Outcome: EngineVerifyOutcome.Running);
        }

        return new EngineEnsureReport(
            false,
            $"engine: running {holder.Version}, expected {version}",
            ActiveEngine: holder,
            Outcome: EngineVerifyOutcome.OtherVersion);
    }

    /// <inheritdoc />
    public Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        return serverState.TryAcquireEngineAsync(stateDir, version, exePath, onShutdownRequested, cancellationToken);
    }

    /// <inheritdoc />
    public void PublishServerInfo(string stateDir, ServerInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(info);
        serverState.WriteServerInfo(stateDir, info);
    }

    /// <inheritdoc />
    public void ClearServerInfo(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        serverState.DeleteServerInfo(stateDir);
    }

    /// <inheritdoc />
    public async Task<Uri?> ResolveLiveServerUriAsync(string? stateDir = null, CancellationToken cancellationToken = default)
    {
        string resolvedStateDir = ResolveStateDirectory(stateDir);
        ServerInfo? published = serverState.ReadServerInfo(resolvedStateDir);
        if (published is { Port: > 0 })
        {
            return LoopbackUri(published.Port);
        }

        (EngineProbeState status, EngineDescriptor? holder) = await serverState
            .ProbeEngineAsync(resolvedStateDir, cancellationToken)
            .ConfigureAwait(false);
        return status == EngineProbeState.Running && holder?.Url is Uri live ? live : null;
    }

    /// <summary>Formats the loopback base URI an engine bound to <paramref name="port"/> answers on.</summary>
    /// <param name="port">TCP port.</param>
    /// <returns>The absolute loopback URI.</returns>
    public static Uri LoopbackUri(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"));
    }

    /// <summary>Resolves the sibling engine binary to launch.</summary>
    /// <param name="apiBinaryPath">Explicit path from the caller, when supplied.</param>
    /// <returns>The path to launch.</returns>
    public static string ResolveApiBinary(string? apiBinaryPath)
    {
        if (!string.IsNullOrWhiteSpace(apiBinaryPath))
        {
            return apiBinaryPath;
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string directory = Path.GetDirectoryName(processPath) ?? string.Empty;
            string candidate = Path.Combine(directory, EngineExecutableName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            return File.Exists(candidate) ? candidate : processPath;
        }

        return EngineExecutableName;
    }

    private static bool TrySpawnDetachedEngine(string exePath, string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);

        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" \"$@\" >/dev/null 2>&1 &");
        startInfo.ArgumentList.Add(exePath);
        startInfo.ArgumentList.Add(NoOpenArgument);
        startInfo.ArgumentList.Add("--state-dir");
        startInfo.ArgumentList.Add(Path.GetFullPath(stateDir));

        try
        {
            _ = ProcessRunner.Run(
                startInfo,
                standardInput: string.Empty,
                timeout: TimeSpan.FromSeconds(10),
                maxOutputBytes: 8192);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private string ResolveStateDirectory(string? stateDir) =>
        string.IsNullOrWhiteSpace(stateDir)
            ? serverState.DefaultStateDirectory
            : Path.GetFullPath(stateDir);
}
