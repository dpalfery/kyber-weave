using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// In-process background coordinator owning the <c>jobs.lock</c> lease, the periodic maintenance
/// timer, and the manual refresh and import triggers.
/// </summary>
/// <remarks>
/// Exactly one process in the fleet holds <c>jobs.lock</c>. That holder runs maintenance; every other
/// host reports <c>running-elsewhere</c> and declines triggers rather than duplicating work or
/// returning a misleading success. Manual refresh honours the <c>jobsPaused</c> setting; import
/// history does not, because an operator asking for history explicitly is not background work.
/// </remarks>
public class JobSchedulerService : IJobSchedulerService
{
    /// <summary>File name of the mutual-exclusion job host lease.</summary>
    public const string JobsLockFile = "jobs.lock";

    /// <summary>File name of the published loopback dashboard server descriptor.</summary>
    public const string ServerInfoFile = "server.json";

    private static readonly TimeSpan TimerStopGrace = TimeSpan.FromSeconds(2);

    /// <summary>Maximum characters of a failure message kept in the status snapshot.</summary>
    public const int MaxFailureSummaryLength = 200;

    /// <summary>Harness whose plugin-captured sessions the counter pass tops up.</summary>
    private const string PluginCapturedHarnessId = "droid";

    /// <summary>OTLP source locator naming the Factory plugin's captured records.</summary>
    private const string PluginCapturedSourceName = "kyberdash-factory-plugin";

    /// <summary>Maximum plugin-captured sessions one pass reads files for.</summary>
    private const int PluginCapturedLimit = 80;

    /// <summary>
    /// Fixed refresh-run id recording that the one-time re-read of units stored with estimated
    /// tokens completed. Its presence is the "already re-read" marker.
    /// </summary>
    /// <remarks>
    /// The relational store has no general metadata table: the pre-rewrite <c>metadata</c> key/value
    /// store was replaced by typed singletons. A completed one-time pass is therefore recorded as a
    /// refresh run under this fixed id, which the existing
    /// <see cref="IOperationalStore.GetRefreshRun"/> reads and
    /// <see cref="IOperationalStore.UpsertRefreshRun"/> writes.
    /// </remarks>
    public const string NoEstimateRereadKey = "kyberdash.no_estimate_reread";

    /// <summary>Harnesses whose estimated-token checkpoints the one-time re-read replaces.</summary>
    private static readonly string[] NoEstimateRereadHarnessIds = ["droid", "cursor", "warp"];

    private readonly IRelationalStoreReader reader;
    private readonly IOperationalStore operationalStore;
    private readonly ICanonProjectionService projection;
    private readonly ITranscriptIngestionService ingestion;
    private readonly ISettingsRepository settings;
    private readonly IServerStateRepository serverState;
    private readonly string stateDir;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string> log;
    private readonly Lock gate = new();
    private readonly Lock timerGate = new();
    private bool timerStopped;

    private IDisposable? lease;
    private Task? timer;
    private string state = "idle";
    private string? lastSuccessAt;
    private string? lastFailure;
    private string? lastMaintenanceFailure;
    private int lastFailedUnits;
    private bool runningJob;
    private bool disposed;

    /// <summary>
    /// Initializes a new <see cref="JobSchedulerService"/>.
    /// </summary>
    /// <param name="reader">Relational reader the maintenance pass reads the store through.</param>
    /// <param name="operationalStore">Operational store whose pending rebuild re-ingest flag <see cref="Start"/> consumes.</param>
    /// <param name="projection">Projection service run by each maintenance pass.</param>
    /// <param name="ingestion">Ingestion service run by an import-history pass.</param>
    /// <param name="settings">Settings repository supplying the pause switch and cadence.</param>
    /// <param name="serverState">Server-state repository owning the jobs lease and the state directory.</param>
    /// <param name="options">Composition options naming the state directory.</param>
    /// <param name="log">Diagnostic logger; defaults to <c>Console.Error.WriteLine</c> when <see langword="null"/>.</param>
    public JobSchedulerService(
        IRelationalStoreReader reader,
        IOperationalStore operationalStore,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion,
        ISettingsRepository settings,
        IServerStateRepository serverState,
        DashOptions options,
        Action<string>? log = null)
        : this(reader, operationalStore, projection, ingestion, settings, serverState, options, clock: null, log)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="JobSchedulerService"/> with an injectable clock.
    /// </summary>
    /// <param name="reader">Relational reader the maintenance pass reads the store through.</param>
    /// <param name="operationalStore">Operational store whose pending rebuild re-ingest flag <see cref="Start"/> consumes.</param>
    /// <param name="projection">Projection service run by each maintenance pass.</param>
    /// <param name="ingestion">Ingestion service run by an import-history pass.</param>
    /// <param name="settings">Settings repository supplying the pause switch and cadence.</param>
    /// <param name="serverState">Server-state repository owning the jobs lease and the state directory.</param>
    /// <param name="options">Composition options naming the state directory.</param>
    /// <param name="clock">Clock used for status timestamps; defaults to UTC now.</param>
    /// <param name="log">Diagnostic logger; defaults to <c>Console.Error.WriteLine</c> when <see langword="null"/>.</param>
    /// <param name="timeProvider">Clock supplying the default timestamp source; defaults to <see cref="TimeProvider.System"/>.</param>
    public JobSchedulerService(
        IRelationalStoreReader reader,
        IOperationalStore operationalStore,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion,
        ISettingsRepository settings,
        IServerStateRepository serverState,
        DashOptions options,
        Func<DateTimeOffset>? clock,
        Action<string>? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serverState);
        ArgumentNullException.ThrowIfNull(options);

        this.reader = reader;
        this.operationalStore = operationalStore;
        this.projection = projection;
        this.ingestion = ingestion;
        this.settings = settings;
        this.serverState = serverState;
        this.clock = clock ?? (timeProvider ?? TimeProvider.System).GetUtcNow;
        this.log = log ?? Console.Error.WriteLine;

        string configured = !string.IsNullOrWhiteSpace(options.StateDirectory)
            ? options.StateDirectory
            : serverState.DefaultStateDirectory;
        stateDir = Path.GetFullPath(configured);
    }

    /// <summary>State directory whose <c>jobs.lock</c> arbitrates this host.</summary>
    public string StateDirectory => stateDir;

    /// <inheritdoc />
    public JobHostStatus GetStatus()
    {
        SettingsView current = settings.ReadSettings();
        lock (gate)
        {
            bool hostedElsewhere = lease is null && !TryEnsureLeaseLocked();
            string reportedState = hostedElsewhere ? "running-elsewhere" : state;
            string? nextDueAt = null;
            if (!current.JobsPaused
                && !hostedElsewhere
                && lastSuccessAt is not null
                && DateTimeOffset.TryParse(lastSuccessAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset last))
            {
                nextDueAt = last.AddMinutes(current.RefreshCadenceMinutes)
                    .UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }

            return new JobHostStatus(
                State: reportedState,
                LastSuccessAt: lastSuccessAt,
                LastFailure: lastFailure,
                NextDueAt: nextDueAt,
                Paused: current.JobsPaused,
                HostedElsewhere: hostedElsewhere,
                LastMaintenanceFailure: lastMaintenanceFailure,
                LastSyncFailure: null,
                LastFailedUnits: lastFailedUnits);
        }
    }

    /// <inheritdoc />
    public Task<JobOutcome> TriggerRefreshAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        JobOutcome outcome = RunJob(work: null);
        return Task.FromResult(outcome);
    }

    /// <inheritdoc />
    public Task<JobOutcome> TriggerImportHistoryAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        JobOutcome outcome = RunJob(work: ImportHistory);
        return Task.FromResult(outcome);
    }

    /// <inheritdoc />
    public void Start()
    {
        bool holdsLease;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lease ??= serverState.TryAcquireJobsLease(stateDir);
            timer ??= Task.Run(() => TimerLoopAsync());
            holdsLease = lease is not null;
        }

        // A store rebuilt from an older schema asks for one full import once a host holds the lease.
        // The check runs outside the gate because RunJob re-enters it.
        if (holdsLease && (operationalStore as SqliteRelationalStore) is { } rebuiltStore
            && rebuiltStore.TryTakePendingRebuildReingest())
        {
            log(DescribeRebuildReingest(rebuiltStore.LastRebuildInfo));
            _ = RunJob(work: ImportHistory);
        }
    }

    /// <summary>
    /// The support line written when a rebuilt store's pending re-ingest is consumed, naming the
    /// rebuild that caused it so an operator who lost history can trace why one import ran.
    /// </summary>
    /// <param name="rebuild">The rebuild summary recorded by the store, when it is still available.</param>
    /// <returns>The logged diagnostic line.</returns>
    private static string DescribeRebuildReingest(StoreRebuildInfo? rebuild)
    {
        if (rebuild is not { } info)
        {
            return "KyberDash rebuilt its relational store from an older schema; running one full import-history pass.";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"KyberDash rebuilt its relational store from schema version {info.OldSchemaVersion} to {info.NewSchemaVersion} at '{info.DatabasePath}'; running one full import-history pass.");
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        Task? running;
        lock (gate)
        {
            running = timer;
            timer = null;
            lease?.Dispose();
            lease = null;
            state = "idle";
        }

        if (running is null)
        {
            return Task.CompletedTask;
        }

        CancelTimer();
        return StopTimerAsync(running);
    }

    /// <summary>
    /// Waits briefly for the timer loop to observe the stop, then leaves it to exit at its next tick.
    /// </summary>
    /// <param name="running">The timer loop task.</param>
    /// <returns>A task that completes once the loop has stopped or the grace period has elapsed.</returns>
    private static async Task StopTimerAsync(Task running)
    {
        Task completed = await Task.WhenAny(running, Task.Delay(TimerStopGrace)).ConfigureAwait(false);
        if (completed == running)
        {
            await running.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the timer and the jobs lease.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lease?.Dispose();
            lease = null;
        }

        CancelTimer();
    }

    private void CancelTimer()
    {
        lock (timerGate)
        {
            if (timerStopped)
            {
                return;
            }

            timerStopped = true;
        }
    }

    /// <summary>Runs one periodic maintenance pass while this host holds the lease.</summary>
    /// <returns>The pass outcome.</returns>
    public JobOutcome RunMaintenancePass()
    {
        lock (gate)
        {
            if (disposed)
            {
                return new JobOutcome("cancelled", "Job host disposed");
            }

            if (!TryEnsureLeaseLocked())
            {
                return new JobOutcome("hosted-elsewhere");
            }

            if (runningJob)
            {
                return new JobOutcome("busy");
            }

            runningJob = true;
            state = "running";
        }

        try
        {
            // A paused host reads no files: neither the scheduled folder import nor the
            // plugin-captured counter pass. The purge and the projection still run so display
            // layers keep moving and content retention stays stamped.
            SettingsView settingsView = settings.ReadSettings();
            HashSet<(string HarnessId, string SourceKey)> failedUnits = [];
            bool readSources = !settingsView.JobsPaused;
            if (readSources)
            {
                // Before anything reads or imports sources, replace once the rows the Droid, Cursor
                // and Warp readers stored from estimated tokens, or clear those estimates to not
                // measured for a unit whose file is gone. The marker makes it a one-time pass
                // across host restarts; a failed read or clear leaves it absent for the next tick.
                if (operationalStore.GetRefreshRun(NoEstimateRereadKey) is null)
                {
                    RereadEstimatedTokenSources(failedUnits);
                }

                if (settingsView.FolderImportScheduled)
                {
                    AddFailedUnits(failedUnits, ingestion.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks));
                }

                AddFailedUnits(failedUnits, RefreshPluginCapturedCounters());
            }

            _ = ingestion.PurgeExpiredContent(clock());
            _ = projection.ProjectAll();

            lock (gate)
            {
                lastMaintenanceFailure = null;
                lastSuccessAt = Timestamp();
                lastFailure = null;
                state = "idle";

                // A paused pass read no source, so it keeps the count of the last pass that did.
                if (readSources)
                {
                    lastFailedUnits = failedUnits.Count;
                }
            }

            return CompletedOutcome(failedUnits.Count);
        }
        catch (Exception exception) when (IsJobFailure(exception))
        {
            string summary = SummarizeFailure(exception.Message);
            lock (gate)
            {
                lastMaintenanceFailure = summary;
                lastFailure = summary;
                state = "failed";
            }

            return new JobOutcome("failed", summary);
        }
        finally
        {
            lock (gate)
            {
                runningJob = false;
            }
        }
    }

    private async Task TimerLoopAsync()
    {
        while (true)
        {
            int minutes;
            try
            {
                minutes = Math.Clamp(settings.ReadSettings().RefreshCadenceMinutes, SettingsService.CadenceMinMinutes, SettingsService.CadenceMaxMinutes);
            }
            catch (Exception exception) when (IsJobFailure(exception))
            {
                minutes = SettingsService.CadenceMaxMinutes;
            }

            await Task.Delay(TimeSpan.FromMinutes(minutes)).ConfigureAwait(false);

            if (IsTimerStopped())
            {
                return;
            }

            _ = RunMaintenancePass();
        }
    }

    /// <summary>Returns whether the timer has been stopped, ending the loop at its next tick.</summary>
    /// <returns><see langword="true"/> when <see cref="CancelTimer"/> has run.</returns>
    private bool IsTimerStopped()
    {
        lock (timerGate)
        {
            return timerStopped;
        }
    }

    /// <summary>Waits until the timer is stopped.</summary>
    /// <returns><see langword="true"/>, meaning the loop should end.</returns>
    private JobOutcome RunJob(Func<RefreshReport>? work)
    {
        if (work is null && settings.ReadSettings().JobsPaused)
        {
            return new JobOutcome("declined", "Background jobs are paused");
        }

        lock (gate)
        {
            if (disposed)
            {
                return new JobOutcome("cancelled", "Job host disposed");
            }

            if (!TryEnsureLeaseLocked())
            {
                return new JobOutcome("hosted-elsewhere");
            }

            if (runningJob)
            {
                return new JobOutcome("busy");
            }

            runningJob = true;
            state = "running";
        }

        try
        {
            HashSet<(string HarnessId, string SourceKey)> failedUnits = [];
            if (work is not null)
            {
                AddFailedUnits(failedUnits, work());
            }

            AddFailedUnits(failedUnits, RefreshPluginCapturedCounters());
            _ = ingestion.PurgeExpiredContent(clock());
            _ = projection.ProjectAll();

            lock (gate)
            {
                lastSuccessAt = Timestamp();
                lastFailure = null;
                state = "idle";
                lastFailedUnits = failedUnits.Count;
            }

            return CompletedOutcome(failedUnits.Count);
        }
        catch (Exception exception) when (IsJobFailure(exception))
        {
            string summary = SummarizeFailure(exception.Message);
            lock (gate)
            {
                lastFailure = summary;
                state = "failed";
            }

            return new JobOutcome("failed", summary);
        }
        finally
        {
            lock (gate)
            {
                runningJob = false;
            }
        }
    }

    /// <summary>Runs the history import and returns its refresh report.</summary>
    private RefreshReport ImportHistory() => ingestion.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);

    /// <summary>
    /// Adds the source units <paramref name="report"/> recorded as failed to <paramref name="failedUnits"/>;
    /// each one was skipped, not imported.
    /// </summary>
    /// <remarks>
    /// One pass can read the same unit more than once (the re-read, the folder import and the plugin
    /// pass), so units are keyed by harness and source key and a unit that failed twice counts once.
    /// </remarks>
    private static void AddFailedUnits(HashSet<(string HarnessId, string SourceKey)> failedUnits, RefreshReport? report)
    {
        if (report is null)
        {
            return;
        }

        foreach (RefreshSourceStatusRow row in report.Rows)
        {
            if (string.Equals(row.Status, "failed", StringComparison.Ordinal))
            {
                _ = failedUnits.Add((row.HarnessId, row.SourceKey));
            }
        }
    }

    /// <summary>
    /// The outcome of a pass that completed. A pass in which source units failed is <c>partial</c>, so the
    /// caller never reads it as a clean success.
    /// </summary>
    private static JobOutcome CompletedOutcome(int failedUnits) =>
        failedUnits > 0 ? new JobOutcome("partial", FailedUnits: failedUnits) : new JobOutcome("succeeded");

    /// <summary>
    /// The plugin-captured counter pass: asks the store which Droid sessions the Factory plugin
    /// captured, then reads only those sessions' transcript files so their counters and models
    /// land on every refresh and every maintenance tick. Runs under the caller's job guard, so
    /// it never overlaps a folder import; a thrown failure here fails the caller's job.
    /// </summary>
    /// <returns>The pass report, whose failed units the caller counts into its outcome.</returns>
    private RefreshReport RefreshPluginCapturedCounters()
    {
        IReadOnlyList<string> sessionKeys = reader.SessionKeysWithRecordSource(
            PluginCapturedHarnessId,
            PluginCapturedSourceName,
            PluginCapturedLimit);
        return ingestion.RefreshPluginSessionFiles(PluginCapturedHarnessId, sessionKeys);
    }

    /// <summary>
    /// Runs the one-time re-read over the Droid, Cursor and Warp readers and records the completion
    /// marker only when the pass reported no failed unit.
    /// </summary>
    /// <remarks>
    /// A failed unit is reported in the pass report rather than thrown, so checking its exit code is
    /// what keeps the marker absent and makes the next tick retry the replacement. Its failed units
    /// are added to <paramref name="failedUnits"/> so the pass reports them.
    /// </remarks>
    /// <param name="failedUnits">The pass's failed units, keyed by harness and source key.</param>
    private void RereadEstimatedTokenSources(HashSet<(string HarnessId, string SourceKey)> failedUnits)
    {
        RefreshReport? report = ingestion.RereadCheckpointedSources(NoEstimateRereadHarnessIds);
        AddFailedUnits(failedUnits, report);
        if (report?.ExitCode != 0)
        {
            return;
        }

        MarkNoEstimateRereadComplete();
    }

    /// <summary>Records the one-time re-read as a completed refresh run under <see cref="NoEstimateRereadKey"/>.</summary>
    private void MarkNoEstimateRereadComplete()
    {
        string timestamp = Timestamp();
        operationalStore.UpsertRefreshRun(new RefreshRunRow(
            Id: NoEstimateRereadKey,
            StartedAt: timestamp,
            CompletedAt: timestamp,
            Status: "success",
            Pid: Environment.ProcessId,
            Trigger: "reread",
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"Re-read {string.Join(", ", NoEstimateRereadHarnessIds)} units stored with estimated tokens."),
            HistoryWeeks: CanonRefreshEngine.DefaultHistoryWeeks));
    }

    private bool TryEnsureLeaseLocked()
    {
        if (lease is not null)
        {
            return true;
        }

        lease = serverState.TryAcquireJobsLease(stateDir);
        return lease is not null;
    }

    private string Timestamp() =>
        clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool IsJobFailure(Exception exception) =>
        exception is InvalidOperationException
            or IOException
            or JsonException
            or ArgumentException
            or UnauthorizedAccessException
            or KyberDash.Core.Exceptions.DashException;

    private static string SummarizeFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Job failed";
        }

        string singleLine = message.ReplaceLineEndings(" ").Trim();
        return singleLine.Length > MaxFailureSummaryLength ? singleLine[..MaxFailureSummaryLength] : singleLine;
    }
}
