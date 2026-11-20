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

    private readonly IRelationalStoreReader reader;
    private readonly IOperationalStore operationalStore;
    private readonly ICanonProjectionService projection;
    private readonly ITranscriptIngestionService ingestion;
    private readonly ISettingsRepository settings;
    private readonly IServerStateRepository serverState;
    private readonly string stateDir;
    private readonly Func<DateTimeOffset> clock;
    private readonly Lock gate = new();
    private readonly Lock timerGate = new();
    private bool timerStopped;

    private IDisposable? lease;
    private Task? timer;
    private string state = "idle";
    private string? lastSuccessAt;
    private string? lastFailure;
    private string? lastMaintenanceFailure;
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
    public JobSchedulerService(
        IRelationalStoreReader reader,
        IOperationalStore operationalStore,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion,
        ISettingsRepository settings,
        IServerStateRepository serverState,
        DashOptions options)
        : this(reader, operationalStore, projection, ingestion, settings, serverState, options, clock: null)
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
    public JobSchedulerService(
        IRelationalStoreReader reader,
        IOperationalStore operationalStore,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion,
        ISettingsRepository settings,
        IServerStateRepository serverState,
        DashOptions options,
        Func<DateTimeOffset>? clock)
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
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

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
                LastSyncFailure: null);
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
        if (holdsLease && (operationalStore as SqliteRelationalStore)?.TryTakePendingRebuildReingest() == true)
        {
            _ = RunJob(work: ImportHistory);
        }
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
            _ = ingestion.PurgeExpiredContent(clock());
            _ = projection.ProjectAll();

            lock (gate)
            {
                lastMaintenanceFailure = null;
                lastSuccessAt = Timestamp();
                lastFailure = null;
                state = "idle";
            }

            return new JobOutcome("succeeded");
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
    private JobOutcome RunJob(Action? work)
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
            work?.Invoke();
            _ = ingestion.PurgeExpiredContent(clock());
            _ = projection.ProjectAll();

            lock (gate)
            {
                lastSuccessAt = Timestamp();
                lastFailure = null;
                state = "idle";
            }

            return new JobOutcome("succeeded");
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

    private void ImportHistory()
    {
        _ = settings.ReadSettings();
        _ = ingestion.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);
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
