using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Fail-closed reader and validated writer for shared operator switches, plus the local Factory plugin
/// consent file and the Antigravity status-line cache.
/// </summary>
public class SettingsRepository : ISettingsRepository
{
    /// <summary>Metadata key controlling scheduled folder imports.</summary>
    public const string FolderImportScheduledKey = "settings.folder_import.scheduled";

    /// <summary>Metadata key controlling whether background jobs are paused.</summary>
    public const string JobsPausedKey = "settings.jobs.paused";

    /// <summary>Metadata key holding the refresh cadence in minutes (<c>1..1440</c>).</summary>
    public const string RefreshCadenceMinutesKey = "settings.jobs.refresh_cadence_minutes";

    /// <summary>Metadata key tracking whether the OTLP receiver is hosted inside the daemon.</summary>
    public const string ReceiverHostedKey = "settings.receiver.hosted";

    /// <summary>Metadata key holding the configured dashboard TCP loopback port (<c>1..65535</c>).</summary>
    public const string DashboardPortKey = "settings.dashboard.port";

    /// <summary>File name of the Factory plugin consent record inside the state directory.</summary>
    public const string FactoryPluginFile = "factory-plugin.json";

    /// <summary>Relative path of the Antigravity status-line cache inside the state directory.</summary>
    public const string AntigravityStatusLineRelativePath = "cache/antigravity-statusline.jsonl";

    /// <summary>Default dashboard loopback TCP port (<c>4747</c>).</summary>
    public const int DefaultDashboardPort = 4747;

    /// <summary>Minimum accepted refresh cadence in minutes.</summary>
    public const int CadenceMinMinutes = 1;

    /// <summary>Maximum accepted refresh cadence in minutes (24 hours).</summary>
    public const int CadenceMaxMinutes = 1440;

    /// <summary>Default refresh cadence in minutes.</summary>
    public const int DefaultRefreshCadenceMinutes = 5;

    /// <summary>Minimum addressable TCP port (<c>1</c>; <c>0</c> is ephemeral and never persisted).</summary>
    public const int PortMin = 1;

    /// <summary>Maximum addressable TCP port (<c>65535</c>).</summary>
    public const int PortMax = 65535;

    /// <summary>JSON property carrying the content-capture consent flag in the Factory plugin file.</summary>
    public const string FactoryContentConsentProperty = "contentCapture";

    private readonly IOperationalStore store;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a repository backed by <paramref name="store"/>.</summary>
    /// <param name="store">Operational store holding the shared operator switches.</param>
    /// <param name="timeProvider">Clock supplying the consent record's update timestamp; defaults to <see cref="TimeProvider.System"/>.</param>
    public SettingsRepository(IOperationalStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SettingsView ReadSettings()
    {
        SettingsView stored = store.ReadSettings();
        int refreshCadenceMinutes = stored.RefreshCadenceMinutes is >= CadenceMinMinutes and <= CadenceMaxMinutes
            ? stored.RefreshCadenceMinutes
            : DefaultRefreshCadenceMinutes;
        int dashboardPort = stored.DashboardPort is >= PortMin and <= PortMax
            ? stored.DashboardPort
            : DefaultDashboardPort;

        return stored with
        {
            RefreshCadenceMinutes = refreshCadenceMinutes,
            DashboardPort = dashboardPort,
        };
    }

    /// <inheritdoc />
    public void WriteBooleanSetting(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key is not (FolderImportScheduledKey or JobsPausedKey or ReceiverHostedKey))
        {
            throw new ArgumentException($"setting '{key}' rejected: not a recognized boolean setting key", nameof(key));
        }

        SettingsView current = ReadSettings();
        SettingsView updated = key switch
        {
            FolderImportScheduledKey => current with { FolderImportScheduled = value },
            JobsPausedKey => current with { JobsPaused = value },
            ReceiverHostedKey => current with { ReceiverHosted = value },
            _ => throw new ArgumentException($"setting '{key}' rejected: not a recognized boolean setting key", nameof(key)),
        };
        store.SaveSettings(updated);
    }

    /// <inheritdoc />
    public void WriteIntSetting(string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.Equals(key, RefreshCadenceMinutesKey, StringComparison.Ordinal))
        {
            if (value is < CadenceMinMinutes or > CadenceMaxMinutes)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "refresh cadence must be between 1 and 1440 minutes");
            }
        }
        else if (string.Equals(key, DashboardPortKey, StringComparison.Ordinal))
        {
            if (value is < PortMin or > PortMax)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "dashboard port must be between 1 and 65535");
            }
        }
        else
        {
            throw new ArgumentException($"setting '{key}' rejected: not a recognized integer setting key", nameof(key));
        }

        SettingsView current = ReadSettings();
        SettingsView updated = string.Equals(key, RefreshCadenceMinutesKey, StringComparison.Ordinal)
            ? current with { RefreshCadenceMinutes = value }
            : current with { DashboardPort = value };
        store.SaveSettings(updated);
    }

    /// <inheritdoc />
    public bool ReadFactoryContentConsent(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string path = Path.Combine(Path.GetFullPath(stateDir), FactoryPluginFile);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(FactoryContentConsentProperty, out JsonElement consentEl)
                && consentEl.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Consent is fail-closed: an unreadable or malformed record never enables capture.
            return false;
        }
    }

    /// <inheritdoc />
    public void WriteFactoryContentConsent(string stateDir, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        Directory.CreateDirectory(fullDir);

        string path = Path.Combine(fullDir, FactoryPluginFile);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contentCapture = enabled,
            updatedAt = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        });

        AtomicFile.Write(path, json);
    }

    /// <inheritdoc />
    public void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string cacheDir = Path.Combine(fullDir, "cache");
        Directory.CreateDirectory(cacheDir);

        string path = Path.Combine(fullDir, AntigravityStatusLineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string line = payload.ValueKind == JsonValueKind.Undefined ? "{}" : payload.GetRawText();
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }
}
