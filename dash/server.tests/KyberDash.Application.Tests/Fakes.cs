using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Application;
using KyberDash.Domain;

namespace KyberDash.Application.Tests;

/// <summary>Shared serializer options for the test fakes.</summary>
internal static class JsonDefaults
{
    /// <summary>Camel-cased options matching the store's persisted JSON.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>In-memory <see cref="IPricingRepository"/> holding a hand-built rate table.</summary>
public class FakePricingRepository : IPricingRepository
{
    private readonly PricingTables tables;
    private readonly JsonElement catalog;

    /// <summary>Initializes a repository over the table built by <see cref="CanonPricing.BuildPricingTables"/>.</summary>
    /// <param name="snapshot">Published model rates.</param>
    /// <param name="fallback">Fallback model rates.</param>
    public FakePricingRepository(
        IReadOnlyDictionary<string, ModelCosts>? snapshot = null,
        IReadOnlyDictionary<string, ModelCosts>? fallback = null)
    {
        tables = CanonPricing.BuildPricingTables(
            snapshot ?? new Dictionary<string, ModelCosts>(StringComparer.Ordinal),
            fallback ?? new Dictionary<string, ModelCosts>(StringComparer.Ordinal));
        catalog = JsonSerializer.SerializeToElement(new { source = "fake", models = tables.SortedKeys.Count });
    }

    /// <summary>Number of times the tables were asked for.</summary>
    public int LoadCalls { get; private set; }

    /// <inheritdoc />
    public PricingTables LoadPricingTables()
    {
        LoadCalls++;
        return tables;
    }

    /// <inheritdoc />
    public JsonElement GetModelCatalogSnapshot() => catalog;
}

/// <summary>In-memory <see cref="ISettingsRepository"/> with the production decoding and bounds.</summary>
public class FakeSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string> metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> consents = new(StringComparer.Ordinal);
    private readonly List<JsonElement> statusLines = [];

    /// <summary>Boolean settings written, keyed by metadata key.</summary>
    public IReadOnlyDictionary<string, string> Metadata => metadata;

    /// <summary>Status-line events recorded so far.</summary>
    public IReadOnlyList<JsonElement> StatusLines => statusLines;

    /// <inheritdoc />
    public SettingsView ReadSettings() =>
        new(
            FolderImportScheduled: string.Equals(Get(SettingsService.FolderImportScheduledKey), "on", StringComparison.Ordinal),
            JobsPaused: string.Equals(Get(SettingsService.JobsPausedKey), "on", StringComparison.Ordinal),
            RefreshCadenceMinutes: DecodeBoundedInt(Get(SettingsService.RefreshCadenceMinutesKey), SettingsService.CadenceMinMinutes, SettingsService.CadenceMaxMinutes, 5),
            ReceiverHosted: string.Equals(Get(SettingsService.ReceiverHostedKey), "on", StringComparison.Ordinal),
            DashboardPort: DecodeBoundedInt(Get(SettingsService.DashboardPortKey), SettingsService.PortMin, SettingsService.PortMax, 4747));

    /// <inheritdoc />
    public void WriteBooleanSetting(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        metadata[key] = value ? "on" : "off";
    }

    /// <inheritdoc />
    public void WriteIntSetting(string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        metadata[key] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public bool ReadFactoryContentConsent(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        return consents.TryGetValue(Path.GetFullPath(stateDir), out bool enabled) && enabled;
    }

    /// <inheritdoc />
    public void WriteFactoryContentConsent(string stateDir, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        consents[Path.GetFullPath(stateDir)] = enabled;
    }

    /// <inheritdoc />
    public void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        statusLines.Add(payload);
    }

    private string? Get(string key) => metadata.TryGetValue(key, out string? value) ? value : null;

    private static int DecodeBoundedInt(string? raw, int min, int max, int fallback) =>
        raw is not null
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= min
            && parsed <= max
            ? parsed
            : fallback;
}

/// <summary><see cref="IServerStateRepository"/> recording publication and returns configurable probes.</summary>
public class FakeServerStateRepository : IServerStateRepository
{
    private readonly Dictionary<string, ServerInfo> serverInfo = new(StringComparer.Ordinal);

    /// <summary>Caller-supplied state directory reported as the default.</summary>
    public FakeServerStateRepository(string? defaultStateDirectory = null) =>
        DefaultStateDirectory = defaultStateDirectory ?? Path.Combine(Path.GetTempPath(), "kyberdash-fake-state");

    /// <inheritdoc />
    public string DefaultStateDirectory { get; }

    /// <summary>Probe result returned by <see cref="ProbeEngineAsync"/>.</summary>
    public (EngineProbeState State, EngineDescriptor? Holder) ProbeResult { get; set; } = (EngineProbeState.NotRunning, null);

    /// <summary>Whether <see cref="RequestEngineShutdownAsync"/> reports success.</summary>
    public bool ShutdownAccepted { get; set; } = true;

    /// <summary>Number of shutdown requests received.</summary>
    public int ShutdownRequests { get; private set; }

    /// <summary>Probe results returned in order, falling back to <see cref="ProbeResult"/> when exhausted.</summary>
    public Queue<(EngineProbeState State, EngineDescriptor? Holder)> ProbeSequence { get; } = new();

    /// <summary>Whether the jobs lease is available.</summary>
    public bool JobsLeaseAvailable { get; set; } = true;

    /// <summary>Number of times the jobs lease was requested.</summary>
    public int JobsLeaseRequests { get; private set; }

    /// <inheritdoc />
    public ServerInfo? ReadServerInfo(string stateDir) =>
        serverInfo.TryGetValue(Path.GetFullPath(stateDir), out ServerInfo? info) ? info : null;

    /// <inheritdoc />
    public void WriteServerInfo(string stateDir, ServerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        serverInfo[Path.GetFullPath(stateDir)] = info;
    }

    /// <inheritdoc />
    public void DeleteServerInfo(string stateDir) => _ = serverInfo.Remove(Path.GetFullPath(stateDir));

    /// <inheritdoc />
    public EngineDescriptor? ReadEngineDescriptor(string stateDir) => null;

    /// <inheritdoc />
    public void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor)
    {
    }

    /// <inheritdoc />
    public void DeleteEngineDescriptor(string stateDir)
    {
    }

    /// <inheritdoc />
    public Task<(EngineProbeState State, EngineDescriptor? Holder)> ProbeEngineAsync(
        string stateDir,
        CancellationToken cancellationToken = default)
    {
        _ = stateDir;
        _ = cancellationToken;
        return Task.FromResult(ProbeSequence.Count > 0 ? ProbeSequence.Dequeue() : ProbeResult);
    }

    /// <inheritdoc />
    public Task<bool> RequestEngineShutdownAsync(string stateDir, CancellationToken cancellationToken = default)
    {
        _ = stateDir;
        _ = cancellationToken;
        ShutdownRequests++;
        return Task.FromResult(ShutdownAccepted);
    }

    /// <inheritdoc />
    public Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default)
    {
        _ = version;
        _ = exePath;
        _ = onShutdownRequested;
        _ = cancellationToken;
        return Task.FromResult<(IEngineOwnerLease?, EngineDescriptor?)>((null, ProbeResult.Holder));
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireJobsLease(string stateDir)
    {
        _ = stateDir;
        JobsLeaseRequests++;
        return JobsLeaseAvailable ? new NoopLease() : null;
    }

    /// <summary>A lease handle that does nothing, standing in for the lock file.</summary>
    public class NoopLease : IDisposable
    {
        /// <summary>Whether the lease was released.</summary>
        public bool Released { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            Released = true;
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary><see cref="ITranscriptReaderRepository"/> returning prepared sources and envelopes.</summary>
public class FakeTranscriptReaderRepository : ITranscriptReaderRepository
{
    /// <summary>Sources returned by <see cref="DiscoverLocalSources"/>.</summary>
    public IReadOnlyList<RefreshSourceInput> Sources { get; set; } = [];

    /// <summary>Envelope lookbacks requested by the caller.</summary>
    public List<DateTimeOffset?> DiscoverCalls { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
        string filePath,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = sourceKey;
        _ = sourceRevision;
        _ = importedAt;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath)
    {
        _ = filePath;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null)
    {
        _ = filePath;
        _ = sourceKey;
        _ = sourceRevision;
        _ = importedAt;
        _ = windowStart;
        _ = windowEnd;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null)
    {
        DiscoverCalls.Add(sinceUtc);
        return Sources;
    }
}
