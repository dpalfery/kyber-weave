using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Dash;

/// <summary>
/// Typed view of operator settings persisted in <c>canon.db</c> <c>metadata</c>
/// (<c>dash/src/settings/shared-settings.ts</c>, <c>dash/src/server/settings-view.ts</c>).
/// </summary>
public sealed record SettingsView(
    [property: JsonPropertyName("folderImportScheduled")] bool FolderImportScheduled,
    [property: JsonPropertyName("jobsPaused")] bool JobsPaused,
    [property: JsonPropertyName("refreshCadenceMinutes")] int RefreshCadenceMinutes,
    [property: JsonPropertyName("receiverHosted")] bool ReceiverHosted,
    [property: JsonPropertyName("dashboardPort")] int DashboardPort);

/// <summary>
/// Fail-closed reader and validated writer for shared operator switches in <see cref="CanonStore"/>.
/// </summary>
public static class CanonSettings
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

    /// <summary>Reads all shared settings from <paramref name="store"/>, failing closed to defaults on unrecognized values.</summary>
    public static SettingsView ReadSettings(CanonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new SettingsView(
            FolderImportScheduled: DecodeOnOff(store.GetMetadata(FolderImportScheduledKey)),
            JobsPaused: DecodeOnOff(store.GetMetadata(JobsPausedKey)),
            RefreshCadenceMinutes: DecodeBoundedInt(
                store.GetMetadata(RefreshCadenceMinutesKey),
                CadenceMinMinutes,
                CadenceMaxMinutes,
                DefaultRefreshCadenceMinutes),
            ReceiverHosted: DecodeOnOff(store.GetMetadata(ReceiverHostedKey)),
            DashboardPort: DecodeBoundedInt(
                store.GetMetadata(DashboardPortKey),
                PortMin,
                PortMax,
                DefaultDashboardPort));
    }

    /// <summary>Writes a single boolean setting after validating <paramref name="key"/>.</summary>
    public static void WriteBooleanSetting(CanonStore store, string key, bool value)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key is not (FolderImportScheduledKey or JobsPausedKey or ReceiverHostedKey))
            throw new ArgumentException($"setting '{key}' rejected: not a recognized boolean setting key", nameof(key));

        store.SetMetadata(key, value ? "on" : "off");
    }

    /// <summary>Writes a single integer setting after validating bounds for <paramref name="key"/>.</summary>
    public static void WriteIntSetting(CanonStore store, string key, int value)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.Equals(key, RefreshCadenceMinutesKey, StringComparison.Ordinal))
        {
            if (value < CadenceMinMinutes || value > CadenceMaxMinutes)
                throw new ArgumentOutOfRangeException(nameof(value), value, "refresh cadence must be between 1 and 1440 minutes");
        }
        else if (string.Equals(key, DashboardPortKey, StringComparison.Ordinal))
        {
            if (value < PortMin || value > PortMax)
                throw new ArgumentOutOfRangeException(nameof(value), value, "dashboard port must be between 1 and 65535");
        }
        else
        {
            throw new ArgumentException($"setting '{key}' rejected: not a recognized integer setting key", nameof(key));
        }

        store.SetMetadata(key, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Validates every entry in <paramref name="patch"/> before writing any change to <paramref name="store"/>,
    /// leaving unmentioned keys untouched.
    /// </summary>
    public static bool TryApplyPartialSettings(
        CanonStore store,
        JsonElement patch,
        out SettingsView? updated,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(store);
        updated = null;
        error = null;

        if (patch.ValueKind != JsonValueKind.Object)
        {
            error = "Settings patch must be a JSON object";
            return false;
        }

        Dictionary<string, object> validatedWrites = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in patch.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "folderImportScheduled":
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = "folderImportScheduled must be a boolean";
                        return false;
                    }

                    validatedWrites[FolderImportScheduledKey] = prop.Value.GetBoolean();
                    break;

                case "jobsPaused":
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = "jobsPaused must be a boolean";
                        return false;
                    }

                    validatedWrites[JobsPausedKey] = prop.Value.GetBoolean();
                    break;

                case "receiverHosted":
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = "receiverHosted must be a boolean";
                        return false;
                    }

                    validatedWrites[ReceiverHostedKey] = prop.Value.GetBoolean();
                    break;

                case "refreshCadenceMinutes":
                    if (!TryGetStrictInteger(prop.Value, out int cadence)
                        || cadence < CadenceMinMinutes
                        || cadence > CadenceMaxMinutes)
                    {
                        error = string.Create(
                            CultureInfo.InvariantCulture,
                            $"refreshCadenceMinutes must be an integer between {CadenceMinMinutes} and {CadenceMaxMinutes}");
                        return false;
                    }

                    validatedWrites[RefreshCadenceMinutesKey] = cadence;
                    break;

                case "dashboardPort":
                    if (!TryGetStrictInteger(prop.Value, out int port)
                        || port < PortMin
                        || port > PortMax)
                    {
                        error = string.Create(
                            CultureInfo.InvariantCulture,
                            $"dashboardPort must be an integer between {PortMin} and {PortMax}");
                        return false;
                    }

                    validatedWrites[DashboardPortKey] = port;
                    break;

                default:
                    error = $"Unknown setting '{prop.Name}'";
                    return false;
            }
        }

        foreach (KeyValuePair<string, object> entry in validatedWrites)
        {
            if (entry.Value is bool b)
                WriteBooleanSetting(store, entry.Key, b);
            else if (entry.Value is int n)
                WriteIntSetting(store, entry.Key, n);
        }

        updated = ReadSettings(store);
        return true;
    }

    private static bool DecodeOnOff(string? raw)
    {
        return string.Equals(raw, "on", StringComparison.Ordinal);
    }

    private static int DecodeBoundedInt(string? raw, int min, int max, int fallback)
    {
        if (string.IsNullOrEmpty(raw))
            return fallback;

        foreach (char ch in raw)
        {
            if (ch is < '0' or > '9')
                return fallback;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= min
            && parsed <= max)
        {
            return parsed;
        }

        return fallback;
    }

    private static bool TryGetStrictInteger(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        string rawText = element.GetRawText();
        if (rawText.Contains('.', StringComparison.Ordinal)
            || rawText.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return element.TryGetInt32(out value);
    }
}
