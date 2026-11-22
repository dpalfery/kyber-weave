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

    private static readonly JsonSerializerOptions CamelCaseOptions = new(JsonSerializerDefaults.Web);

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
        string platform = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";

        var receiver = new
        {
            hosted = current.ReceiverHosted,
            liveness = current.ReceiverHosted ? "kyberdash" : "none",
            detail = current.ReceiverHosted
                ? $"KyberDash receiver is listening at http://127.0.0.1:{OtlpCollector.DefaultOtlpPort}"
                : "this KyberDash server does not host the OTLP receiver; liveness was not probed",
        };

        List<HarnessStatusView> harnessesList = BuildHarnessStatuses(factoryConsent);
        int enabledCount = harnessesList.Count(static h => h.State == "on");

        var check = new
        {
            checkedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            health = enabledCount > 0 ? "ok" : "off",
            enabled = enabledCount,
            attention = 0,
            issueCount = 0,
        };

        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            receiverHosted = current.ReceiverHosted,
            jobsPaused = current.JobsPaused,
            otlpEndpoint = OtlpEndpointLabel,
            contentCapture = factoryConsent,
            platform,
            receiver,
            harnesses = harnessesList,
            check,
            stateDirectory = Path.GetFullPath(stateDir),
        }, CamelCaseOptions);
    }

    private static List<HarnessStatusView> BuildHarnessStatuses(bool factoryConsent)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<HarnessStatusView> list = [];

        // 1. Copilot
        string copilotPath = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Code", "User", "settings.json")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json")
                : Path.Combine(home, ".config", "Code", "User", "settings.json");
        bool copilotExists = File.Exists(copilotPath);
        bool copilotOn = false;
        if (copilotExists)
        {
            try
            {
                string text = File.ReadAllText(copilotPath);
                copilotOn = text.Contains("\"github.copilot.chat.otel.enabled\": true", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("\"github.copilot.chat.otel.enabled\":true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        list.Add(new HarnessStatusView(
            Id: "copilot",
            DisplayName: "Copilot",
            Mode: "managed",
            Installed: copilotExists ? "detected" : "not-detected",
            InstallEvidence: copilotExists ? [$"VS Code settings found at {copilotPath}"] : [$"VS Code settings not found at {copilotPath}"],
            State: copilotOn ? "on" : "off",
            Receipt: "absent",
            Files: copilotExists ? [new HarnessFileView("vscode-settings", copilotPath, true, true, ["github.copilot.chat.otel.enabled"], copilotOn ? ["github.copilot.chat.otel.enabled"] : [], copilotOn ? [] : ["github.copilot.chat.otel.enabled"], [], null)] : [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: copilotOn ? ["OTLP export: enabled"] : ["OTLP export: disabled"],
            Health: copilotOn ? "ok" : "off",
            Issues: []
        ));

        // 2. Claude Code
        string claudePath = Path.Combine(home, ".claude", "settings.json");
        bool claudeExists = File.Exists(claudePath);
        bool claudeOn = false;
        if (claudeExists)
        {
            try
            {
                string text = File.ReadAllText(claudePath);
                claudeOn = text.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("OTEL_LOGS_EXPORTER", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        list.Add(new HarnessStatusView(
            Id: "claude-code",
            DisplayName: "Claude Code",
            Mode: "managed",
            Installed: claudeExists ? "detected" : "not-detected",
            InstallEvidence: claudeExists ? [$"config found at {claudePath}"] : [$"no config found at {claudePath}"],
            State: claudeOn ? "on" : "off",
            Receipt: "absent",
            Files: claudeExists ? [new HarnessFileView("claude-settings", claudePath, true, true, ["env.OTEL_EXPORTER_OTLP_ENDPOINT"], claudeOn ? ["env.OTEL_EXPORTER_OTLP_ENDPOINT"] : [], claudeOn ? [] : ["env.OTEL_EXPORTER_OTLP_ENDPOINT"], [], null)] : [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: claudeOn ? ["OTLP export: enabled"] : ["OTLP export: disabled"],
            Health: claudeOn ? "ok" : "off",
            Issues: []
        ));

        // 3. OpenCode
        string opencodeConfig = Path.Combine(home, ".config", "opencode", "opencode.jsonc");
        string opencodePlugin = Path.Combine(home, ".config", "opencode", "plugins", "kyberdash.ts");
        bool opencodeInstalled = File.Exists(opencodeConfig) || File.Exists(opencodePlugin);
        bool opencodeOn = File.Exists(opencodePlugin);
        list.Add(new HarnessStatusView(
            Id: "opencode",
            DisplayName: "OpenCode",
            Mode: "managed",
            Installed: opencodeInstalled ? "detected" : "not-detected",
            InstallEvidence: opencodeInstalled ? [$"config found at {opencodeConfig}"] : [$"no config found at {opencodeConfig}"],
            State: opencodeOn ? "on" : "off",
            Receipt: "absent",
            Files: [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: opencodeOn ? ["plugin: installed"] : ["plugin: not installed"],
            Health: opencodeOn ? "ok" : "off",
            Issues: []
        ));

        // 4. Codex
        string codexConfig = Path.Combine(home, ".codex", "config.toml");
        bool codexExists = File.Exists(codexConfig);
        list.Add(new HarnessStatusView(
            Id: "codex",
            DisplayName: "Codex",
            Mode: "status-only",
            Installed: codexExists ? "detected" : "not-detected",
            InstallEvidence: codexExists ? [$"config found at {codexConfig}"] : [$"no config found at {codexConfig}"],
            State: "not-applicable",
            Receipt: "absent",
            Files: [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: ["coverage comes from session files"],
            Health: "read-only",
            Issues: []
        ));

        // 5. Pi
        string piConfig = Path.Combine(home, ".pi", "agent", "settings.json");
        bool piExists = File.Exists(piConfig);
        list.Add(new HarnessStatusView(
            Id: "pi",
            DisplayName: "Pi",
            Mode: "status-only",
            Installed: piExists ? "detected" : "not-detected",
            InstallEvidence: piExists ? [$"agent settings found at {piConfig}"] : [$"no settings found at {piConfig}"],
            State: "not-applicable",
            Receipt: "absent",
            Files: [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: ["coverage comes from session files"],
            Health: "read-only",
            Issues: []
        ));

        // 6. Factory
        string factoryDir = Environment.GetEnvironmentVariable("FACTORY_DIR") is { Length: > 0 } fDir
            ? fDir
            : Path.Combine(home, ".factory");
        bool factoryExists = Directory.Exists(factoryDir);
        list.Add(new HarnessStatusView(
            Id: "factory",
            DisplayName: "Factory",
            Mode: "managed",
            Installed: factoryExists ? "detected" : "not-detected",
            InstallEvidence: factoryExists ? [$"directory found at {factoryDir}"] : [$"directory not found at {factoryDir}"],
            State: factoryConsent ? "on" : "off",
            Receipt: "absent",
            Files: [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: factoryConsent ? ["content consent: on"] : ["content consent: off"],
            Health: factoryConsent ? "ok" : "off",
            Issues: []
        ));

        return list;
    }

    /// <summary>File-level status entry for harness configuration.</summary>
    internal record HarnessFileView(
        string Id,
        string Path,
        bool Applicable,
        bool? Exists,
        IReadOnlyList<string> ExpectedKeys,
        IReadOnlyList<string> PresentKeys,
        IReadOnlyList<string> MissingKeys,
        IReadOnlyList<string> MismatchedKeys,
        object? Drift);

    /// <summary>Status entry for one AI coding harness.</summary>
    internal record HarnessStatusView(
        string Id,
        string DisplayName,
        string Mode,
        string Installed,
        IReadOnlyList<string> InstallEvidence,
        string State,
        string Receipt,
        IReadOnlyList<HarnessFileView> Files,
        IReadOnlyList<object> Warnings,
        IReadOnlyList<object> ManualSteps,
        IReadOnlyList<string> StatusLines,
        string? Health,
        IReadOnlyList<object> Issues);

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
