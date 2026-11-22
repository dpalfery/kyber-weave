using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Contracts.Repositories;

/// <summary>Streaming transcript reader contract decoupling JSONL filesystem scanning from application synthesis.</summary>
public interface ITranscriptReaderRepository
{
    /// <summary>Reads Claude Code JSONL transcript turns from <paramref name="filePath"/>.</summary>
    IReadOnlyList<ClaudeTurnGroup> ReadClaudeTurns(
        string filePath,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Reads Claude Code JSONL transcript turns as classified <see cref="SourceRecordEnvelope"/> entries.</summary>
    IReadOnlyList<SourceRecordEnvelope> ReadClaudeEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Reads Codex JSONL transcript turns from <paramref name="filePath"/>.</summary>
    IReadOnlyList<ReaderTurn> ReadCodexTurns(string filePath);

    /// <summary>Reads Codex JSONL transcript turns as classified <see cref="SourceRecordEnvelope"/> entries.</summary>
    IReadOnlyList<SourceRecordEnvelope> ReadCodexEnvelopes(
        string filePath,
        string? sourceKey = null,
        string? sourceRevision = null,
        string? importedAt = null,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null);

    /// <summary>Discovers local harness transcript sources within <paramref name="sinceUtc"/> lookback.</summary>
    IReadOnlyList<RefreshSourceInput> DiscoverLocalSources(DateTimeOffset? sinceUtc = null);
}

/// <summary>Model rate-table repository contract separating embedded snapshot loading from pricing math.</summary>
public interface IPricingRepository
{
    /// <summary>Loads or returns cached published and fallback model rate tables.</summary>
    PricingTables LoadPricingTables();

    /// <summary>Returns catalog refresh status metadata persisted alongside model context windows.</summary>
    JsonElement GetModelCatalogSnapshot();
}

/// <summary>Operator settings and local plugin consent persistence contract.</summary>
public interface ISettingsRepository
{
    /// <summary>Reads shared operator settings from the operational store.</summary>
    SettingsView ReadSettings();

    /// <summary>Persists a validated boolean setting.</summary>
    void WriteBooleanSetting(string key, bool value);

    /// <summary>Persists a validated integer setting.</summary>
    void WriteIntSetting(string key, int value);

    /// <summary>Reads Factory plugin content-capture consent from <c>factory-plugin.json</c>.</summary>
    bool ReadFactoryContentConsent(string stateDir);

    /// <summary>Writes Factory plugin content-capture consent to <c>factory-plugin.json</c>.</summary>
    void WriteFactoryContentConsent(string stateDir, bool enabled);

    /// <summary>Appends one Antigravity statusline event line to the local JSONL cache.</summary>
    void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload);
}

/// <summary>Process coordination, descriptor publication, and exclusive lock lease repository contract.</summary>
public interface IServerStateRepository
{
    /// <summary>Default state directory path (<c>~/.kyberdash</c>).</summary>
    string DefaultStateDirectory { get; }

    /// <summary>Reads <c>server.json</c> from <paramref name="stateDir"/> when present and valid.</summary>
    ServerInfo? ReadServerInfo(string stateDir);

    /// <summary>Writes <c>server.json</c> atomically with <c>0600</c> permissions and symlink refusal.</summary>
    void WriteServerInfo(string stateDir, ServerInfo info);

    /// <summary>Deletes <c>server.json</c> when present.</summary>
    void DeleteServerInfo(string stateDir);

    /// <summary>Reads <c>engine.json</c> from <paramref name="stateDir"/> when present.</summary>
    EngineDescriptor? ReadEngineDescriptor(string stateDir);

    /// <summary>Writes <c>engine.json</c> atomically with <c>0600</c> permissions and symlink refusal.</summary>
    void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor);

    /// <summary>Deletes <c>engine.json</c> when present.</summary>
    void DeleteEngineDescriptor(string stateDir);

    /// <summary>Probes whether a running <c>kyberdash-api</c> instance answers on its control endpoint.</summary>
    Task<(EngineProbeState State, EngineDescriptor? Holder)> ProbeEngineAsync(
        string stateDir,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a graceful shutdown request to the live <c>kyberdash-api</c> instance.</summary>
    Task<bool> RequestEngineShutdownAsync(
        string stateDir,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire the single-engine IPC socket lock.</summary>
    Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire the exclusive <c>jobs.lock</c> file lease.</summary>
    IDisposable? TryAcquireJobsLease(string stateDir);
}

/// <summary>Static dashboard SPA asset loader contract supporting disk override and embedded bundles.</summary>
public interface IWebAssetRepository
{
    /// <summary>Attempts to resolve a static file or SPA index asset for <paramref name="requestPath"/>.</summary>
    WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null);
}
