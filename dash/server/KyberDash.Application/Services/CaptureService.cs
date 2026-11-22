using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Capture receiver status, per-harness plugin installation, Factory content consent, and the
/// Antigravity status-line cache (<c>kyberdash</c> capture commands).
/// </summary>
/// <remarks>
/// Installing a harness plugin only reports where the harness expects its plugin: the plugin files
/// themselves ship with the release, and rewriting an operator's editor configuration is the
/// harness's job. Consent is the one thing this service writes on the operator's behalf, and it is
/// fail-closed: an unreadable consent record means capture stays off.
/// </remarks>
public class CaptureService : ICaptureService
{
    /// <summary>Harness identifier for the OpenCode plugin.</summary>
    public const string OpencodeHarness = "opencode";

    /// <summary>Harness identifier for the Factory hook plugin.</summary>
    public const string FactoryHarness = "factory";

    /// <summary>Relative path of the OpenCode plugin file inside its config root.</summary>
    public const string OpencodePluginRelativePath = "plugins/kyberdash.ts";

    /// <summary>Relative path of the Factory plugin directory inside its config root.</summary>
    public const string FactoryPluginRelativePath = "plugins/kyberdash";

    private readonly ISettingsRepository settings;
    private readonly IOperationalStore store;
    private readonly IServerStateRepository serverState;

    /// <summary>
    /// Initializes a new <see cref="CaptureService"/>.
    /// </summary>
    /// <param name="settings">Settings repository holding consent and status-line cache files.</param>
    /// <param name="store">Operational store owning the receiver switch and capture state.</param>
    /// <param name="serverState">Server-state repository supplying the default state directory.</param>
    public CaptureService(
        ISettingsRepository settings,
        IOperationalStore store,
        IServerStateRepository serverState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverState);

        this.settings = settings;
        this.store = store;
        this.serverState = serverState;
    }

    /// <inheritdoc />
    public JsonElement GetCaptureStatus(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);

        SettingsView current = settings.ReadSettings();
        bool factoryConsent = settings.ReadFactoryContentConsent(stateDir);
        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            receiverHosted = current.ReceiverHosted,
            jobsPaused = current.JobsPaused,
            otlpEndpoint = OtlpEndpointLabel,
            contentCapture = factoryConsent,
            harnesses = new
            {
                opencode = new { harness = OpencodeHarness, installed = File.Exists(ResolvePluginFile(OpencodeHarness, null)) },
                factory = new { harness = FactoryHarness, installed = Directory.Exists(ResolvePluginFile(FactoryHarness, null)), contentCapture = factoryConsent },
            },
            stateDirectory = Path.GetFullPath(stateDir),
        });
    }

    /// <inheritdoc />
    public JsonElement ConfigureCapture(JsonElement body, string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DashValidationException("CAPTURE_INVALID", "Capture update must be a JSON object");
        }

        bool? receiverHosted = ReadOptionalBoolean(body, "receiverHosted");
        bool? contentCapture = ReadOptionalBoolean(body, "contentCapture");

        if (!receiverHosted.HasValue && !contentCapture.HasValue)
        {
            throw new DashValidationException(
                "CAPTURE_INVALID",
                "Capture update must name receiverHosted or contentCapture");
        }

        if (receiverHosted.HasValue)
        {
            settings.WriteBooleanSetting(SettingsService.ReceiverHostedKey, receiverHosted.Value);
        }

        if (contentCapture.HasValue)
        {
            settings.WriteFactoryContentConsent(stateDir, contentCapture.Value);
        }

        SettingsView updated = settings.ReadSettings();
        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            receiverHosted = updated.ReceiverHosted,
            contentCapture = settings.ReadFactoryContentConsent(stateDir),
        });
    }

    /// <inheritdoc />
    public (int ExitCode, string Output) InstallPlugin(string harness, bool ifInstalled, string? configDir, string? stateDir = null)
    {
        // Plugin installation reports where the harness reads its plugin from; it never needs the state directory.
        _ = stateDir;
        if (configDir is not null && string.Equals(harness, FactoryHarness, StringComparison.Ordinal))
        {
            return (2, "--config-dir selects the OpenCode config root; the Factory harness resolves its own root ($FACTORY_DIR, else ~/.factory). Pass --harness opencode to use it.");
        }

        if (!string.Equals(harness, OpencodeHarness, StringComparison.Ordinal)
            && !string.Equals(harness, FactoryHarness, StringComparison.Ordinal))
        {
            string given = harness is null ? "missing" : $"\"{harness}\" is not supported";
            return (2, $"--harness {given}. Expected one of: opencode, factory.");
        }

        string pluginPath = ResolvePluginFile(harness, configDir);
        bool installed = string.Equals(harness, FactoryHarness, StringComparison.Ordinal)
            ? Directory.Exists(pluginPath)
            : File.Exists(pluginPath);

        if (ifInstalled && !installed)
        {
            return (0, $"{harness} plugin: skipped-not-installed {pluginPath}");
        }

        return (0, $"{harness} plugin: unchanged {pluginPath}");
    }

    /// <inheritdoc />
    public (int ExitCode, string Output) SetFactoryConsent(bool enabled, string? stateDir = null)
    {
        string resolved = ResolveStateDirectory(stateDir);
        settings.WriteFactoryContentConsent(resolved, enabled);
        return (0, $"factory consent: content {(enabled ? "on" : "off")} {Path.Combine(resolved, "factory-plugin.json")}");
    }

    /// <inheritdoc />
    public Task RecordAntigravityStatusLineAsync(
        string rawPayload,
        string? stateDir = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return Task.CompletedTask;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawPayload);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.CompletedTask;
            }

            settings.AppendAntigravityStatusLineEvent(ResolveStateDirectory(stateDir), root.Clone());
        }

        return Task.CompletedTask;
    }

    /// <summary>Default OTLP collector endpoint a harness plugin points at.</summary>
    public static string OtlpEndpointLabel =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{OtlpCollector.DefaultOtlpPort}");

    /// <summary>Resolves the plugin path a harness reads its KyberDash plugin from.</summary>
    /// <param name="harness">Harness identifier.</param>
    /// <param name="configDir">Explicit OpenCode config root, when the caller supplied one.</param>
    /// <returns>Absolute plugin file or directory path.</returns>
    public static string ResolvePluginFile(string harness, string? configDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.Equals(harness, OpencodeHarness, StringComparison.Ordinal))
        {
            string root = !string.IsNullOrWhiteSpace(configDir)
                ? configDir
                : Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } envDir
                    ? envDir
                    : Path.Combine(home, ".config", "opencode");
            return Path.Combine(root, OpencodePluginRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        string factoryDir = Environment.GetEnvironmentVariable("FACTORY_DIR") is { Length: > 0 } explicitDir
            ? explicitDir
            : Path.Combine(home, ".factory");
        return Path.Combine(factoryDir, FactoryPluginRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Default state directory used when a command omits <c>--state-dir</c>.</summary>
    /// <returns>The resolved absolute state directory.</returns>
    public string DefaultStateDirectory => serverState.DefaultStateDirectory;

    /// <summary>Reads an optional boolean property, rejecting a value of any other shape.</summary>
    private static bool? ReadOptionalBoolean(JsonElement body, string propertyName)
    {
        if (!body.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new DashValidationException(
                "CAPTURE_INVALID",
                string.Create(CultureInfo.InvariantCulture, $"{propertyName} must be a boolean")),
        };
    }

    private string ResolveStateDirectory(string? stateDir) =>
        string.IsNullOrWhiteSpace(stateDir) ? serverState.DefaultStateDirectory : Path.GetFullPath(stateDir);
}
