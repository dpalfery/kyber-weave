using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Reads and validates operator settings.
/// </summary>
/// <remarks>
/// A partial update is validated in full before any key is written, so a patch that carries one bad
/// value changes nothing. Recognized keys, accepted value shapes, and numeric bounds follow the
/// shared-settings contract the web dashboard and tray both send.
/// </remarks>
public class SettingsService : ISettingsService
{
    /// <summary>Settings key controlling scheduled folder imports.</summary>
    public const string FolderImportScheduledKey = "settings.folder_import.scheduled";

    /// <summary>Settings key controlling whether background jobs are paused.</summary>
    public const string JobsPausedKey = "settings.jobs.paused";

    /// <summary>Settings key holding the refresh cadence in minutes (<c>1..1440</c>).</summary>
    public const string RefreshCadenceMinutesKey = "settings.jobs.refresh_cadence_minutes";

    /// <summary>Settings key tracking whether the OTLP receiver is hosted inside the daemon.</summary>
    public const string ReceiverHostedKey = "settings.receiver.hosted";

    /// <summary>Settings key holding the configured dashboard TCP loopback port (<c>1..65535</c>).</summary>
    public const string DashboardPortKey = "settings.dashboard.port";

    /// <summary>Minimum accepted refresh cadence in minutes.</summary>
    public const int CadenceMinMinutes = 1;

    /// <summary>Maximum accepted refresh cadence in minutes (24 hours).</summary>
    public const int CadenceMaxMinutes = 1440;

    /// <summary>Minimum addressable TCP port (<c>1</c>; <c>0</c> is ephemeral and never persisted).</summary>
    public const int PortMin = 1;

    /// <summary>Maximum addressable TCP port (<c>65535</c>).</summary>
    public const int PortMax = 65535;

    /// <summary>Error code carried by every rejected settings patch.</summary>
    public const string ValidationErrorCode = "SETTINGS_INVALID";

    private readonly ISettingsRepository settings;

    /// <summary>
    /// Initializes a new <see cref="SettingsService"/>.
    /// </summary>
    /// <param name="settings">Settings repository reading and writing shared operator switches.</param>
    public SettingsService(ISettingsRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <inheritdoc />
    public SettingsView GetSettings() => settings.ReadSettings();

    /// <inheritdoc />
    public SettingsView UpdateSettings(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw Reject("Settings patch must be a JSON object");
        }

        Dictionary<string, object> validatedWrites = new(StringComparer.Ordinal);
        foreach (JsonProperty property in patch.EnumerateObject())
        {
            switch (property.Name)
            {
                case "folderImportScheduled":
                    validatedWrites[FolderImportScheduledKey] = ReadBoolean(property, "folderImportScheduled");
                    break;

                case "jobsPaused":
                    validatedWrites[JobsPausedKey] = ReadBoolean(property, "jobsPaused");
                    break;

                case "receiverHosted":
                    validatedWrites[ReceiverHostedKey] = ReadBoolean(property, "receiverHosted");
                    break;

                case "refreshCadenceMinutes":
                    validatedWrites[RefreshCadenceMinutesKey] = ReadBoundedInteger(
                        property,
                        "refreshCadenceMinutes",
                        CadenceMinMinutes,
                        CadenceMaxMinutes);
                    break;

                case "dashboardPort":
                    validatedWrites[DashboardPortKey] = ReadBoundedInteger(property, "dashboardPort", PortMin, PortMax);
                    break;

                default:
                    throw Reject(string.Create(CultureInfo.InvariantCulture, $"Unknown setting '{property.Name}'"));
            }
        }

        foreach (KeyValuePair<string, object> entry in validatedWrites)
        {
            if (entry.Value is bool flag)
            {
                settings.WriteBooleanSetting(entry.Key, flag);
            }
            else if (entry.Value is int number)
            {
                settings.WriteIntSetting(entry.Key, number);
            }
        }

        return settings.ReadSettings();
    }

    private static bool ReadBoolean(JsonProperty property, string name) =>
        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.Value.GetBoolean()
            : throw Reject(string.Create(CultureInfo.InvariantCulture, $"{name} must be a boolean"));

    private static int ReadBoundedInteger(JsonProperty property, string name, int min, int max) =>
        TryReadStrictInteger(property.Value, out int value) && value >= min && value <= max
            ? value
            : throw Reject(string.Create(
                CultureInfo.InvariantCulture,
                $"{name} must be an integer between {min} and {max}"));

    /// <summary>Reads a JSON number only when it is written as a bare integer (no fraction, no exponent).</summary>
    private static bool TryReadStrictInteger(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string raw = element.GetRawText();
        if (raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return element.TryGetInt32(out value);
    }

    private static DashValidationException Reject(string message) => new(ValidationErrorCode, message);
}
