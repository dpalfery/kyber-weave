using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector.Sources;
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
    private readonly IHarnessFileSystem files;
    private readonly Func<string> homeDirectory;

    /// <summary>
    /// Initializes a new <see cref="CaptureService"/>.
    /// </summary>
    /// <param name="settings">Settings repository holding consent and status-line cache files.</param>
    /// <param name="store">Operational store owning the receiver switch and capture state.</param>
    /// <param name="serverState">Server-state repository supplying the default state directory.</param>
    /// <param name="files">Filesystem seam for harness config probes. The process filesystem is used when omitted.</param>
    /// <param name="homeDirectory">Home directory the probes are rooted at. The operator profile is used when omitted.</param>
    public CaptureService(
        ISettingsRepository settings,
        IOperationalStore store,
        IServerStateRepository serverState,
        IHarnessFileSystem? files = null,
        Func<string>? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverState);

        this.settings = settings;
        this.store = store;
        this.serverState = serverState;
        this.files = files ?? PhysicalHarnessFileSystem.Instance;
        this.homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <inheritdoc />
    public JsonElement GetCaptureStatus(string stateDir, int? otlpPort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);

        SettingsView current = settings.ReadSettings();
        bool factoryConsent = settings.ReadFactoryContentConsent(stateDir);
        string platform = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";
        string endpoint = otlpPort is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{otlpPort.Value}")
            : "the configured collector port";

        var receiver = new
        {
            hosted = current.ReceiverHosted,
            liveness = "unknown",
            detail = current.ReceiverHosted
                ? $"KyberDash receiver hosting is requested at {endpoint}; liveness was not probed"
                : "this KyberDash server does not host the OTLP receiver; liveness was not probed",
        };

        List<HarnessStatusView> harnessesList = BuildHarnessStatuses(factoryConsent);
        int enabledCount = harnessesList.Count(static h => h.State == "on");
        int attention = harnessesList.Count(static h => h.State == "unknown" || h.Health == "attention");
        int issueCount = harnessesList.Sum(static h => h.Issues.Count);
        bool statusOnlyDetected = harnessesList.Exists(static h => h.Mode == "status-only" && h.Installed == "detected");
        string health = attention > 0 || issueCount > 0
            ? "attention"
            : !current.ReceiverHosted && enabledCount > 0
                ? "attention"
                : current.ReceiverHosted && enabledCount > 0
                    ? "ok"
                    : statusOnlyDetected
                        ? "attention"
                        : "off";

        var check = new
        {
            checkedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            health,
            enabled = enabledCount,
            attention,
            issueCount,
        };

        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            receiverHosted = current.ReceiverHosted,
            jobsPaused = current.JobsPaused,
            otlpEndpoint = endpoint,
            contentCapture = factoryConsent,
            platform,
            receiver,
            harnesses = harnessesList,
            check,
            stateDirectory = Path.GetFullPath(stateDir),
        }, CamelCaseOptions);
    }

    /// <summary>
    /// Resolves the VS Code user settings file the Copilot probe reads on this platform.
    /// </summary>
    /// <param name="home">Home directory the probe is rooted at.</param>
    /// <returns>Absolute path of the VS Code user settings file.</returns>
    /// <remarks>
    /// VS Code keeps user settings under <c>Library/Application Support</c> on macOS, the roaming
    /// AppData profile on Windows, and <c>.config</c> on Linux, so the probe and the test proving
    /// it ask this one rule rather than each hard-coding a layout and disagreeing on macOS.
    /// </remarks>
    public static string CopilotSettingsFile(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Code", "User", "settings.json")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json")
                : Path.Combine(home, ".config", "Code", "User", "settings.json");
    }

    private List<HarnessStatusView> BuildHarnessStatuses(bool factoryConsent)
    {
        string home = homeDirectory();
        List<HarnessStatusView> list = [];

        string copilotPath = CopilotSettingsFile(home);
        list.Add(DescribeManagedFile(
            "copilot",
            "Copilot",
            copilotPath,
            "VS Code settings",
            "github.copilot.chat.otel.enabled",
            static root => JsonBoolean(root, "github.copilot.chat.otel.enabled") == true));

        string claudePath = Path.Combine(home, ".claude", "settings.json");
        list.Add(DescribeManagedFile(
            "claude-code",
            "Claude Code",
            claudePath,
            "config",
            "env.OTEL_EXPORTER_OTLP_ENDPOINT",
            static root => ClaudePointsAtLoopback(root)));

        string opencodeConfig = Path.Combine(home, ".config", "opencode", "opencode.jsonc");
        string opencodePlugin = Path.Combine(home, ".config", "opencode", "plugins", "kyberdash.ts");
        bool opencodeConfigExists = files.FileExists(opencodeConfig);
        bool opencodePluginExists = files.FileExists(opencodePlugin);
        List<string> opencodeEvidence = [];
        if (opencodeConfigExists)
            opencodeEvidence.Add($"config found at {opencodeConfig}");
        if (opencodePluginExists)
            opencodeEvidence.Add($"plugin found at {opencodePlugin}");
        if (opencodeEvidence.Count == 0)
            opencodeEvidence.Add($"no config found at {opencodeConfig}");

        // OpenCode enable is the plugin file. The product does not write an OTLP key into opencode.jsonc.
        list.Add(new HarnessStatusView(
            Id: "opencode",
            DisplayName: "OpenCode",
            Mode: "managed",
            Installed: opencodeConfigExists || opencodePluginExists ? "detected" : "not-detected",
            InstallEvidence: opencodeEvidence,
            State: opencodePluginExists ? "on" : "off",
            Receipt: "absent",
            Files: [],
            Warnings: [],
            ManualSteps: [],
            StatusLines: opencodePluginExists ? ["plugin: installed"] : ["plugin: not installed"],
            Health: opencodePluginExists ? "ok" : "off",
            Issues: []
        ));

        // 4. Codex
        string codexConfig = Path.Combine(home, ".codex", "config.toml");
        bool codexExists = files.FileExists(codexConfig);
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
        bool piExists = files.FileExists(piConfig);
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
        bool factoryExists = files.DirectoryExists(factoryDir);
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

    private HarnessStatusView DescribeManagedFile(
        string id,
        string displayName,
        string path,
        string evidenceNoun,
        string key,
        Func<JsonElement, bool> enabled)
    {
        (FileProbe probe, string text) = ProbeFile(path);
        if (probe == FileProbe.Missing)
        {
            return new HarnessStatusView(
                Id: id,
                DisplayName: displayName,
                Mode: "managed",
                Installed: "not-detected",
                InstallEvidence: [$"{evidenceNoun} not found at {path}"],
                State: "off",
                Receipt: "absent",
                Files: [],
                Warnings: [],
                ManualSteps: [],
                StatusLines: ["OTLP export: disabled"],
                Health: "off",
                Issues: []);
        }

        if (probe == FileProbe.Unreadable)
        {
            object warning = new { code = "unreadable", severity = "warning", text = $"{evidenceNoun} at {path} could not be read" };
            return new HarnessStatusView(
                Id: id,
                DisplayName: displayName,
                Mode: "managed",
                Installed: "detected",
                InstallEvidence: [$"{evidenceNoun} found at {path}"],
                State: "unknown",
                Receipt: "absent",
                Files: [new HarnessFileView(id, path, true, true, [key], [], [key], [], null)],
                Warnings: [warning],
                ManualSteps: [],
                StatusLines: ["OTLP export: unknown"],
                Health: "attention",
                Issues: [warning]);
        }

        bool on = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(StripJsonComments(text));
            on = document.RootElement.ValueKind == JsonValueKind.Object && enabled(document.RootElement);
        }
        catch (JsonException)
        {
            object warning = new { code = "malformed", severity = "warning", text = $"{evidenceNoun} at {path} could not be parsed" };
            return new HarnessStatusView(
                Id: id,
                DisplayName: displayName,
                Mode: "managed",
                Installed: "detected",
                InstallEvidence: [$"{evidenceNoun} found at {path}"],
                State: "unknown",
                Receipt: "absent",
                Files: [new HarnessFileView(id, path, true, true, [key], [], [key], [], null)],
                Warnings: [warning],
                ManualSteps: [],
                StatusLines: ["OTLP export: unknown"],
                Health: "attention",
                Issues: [warning]);
        }

        return new HarnessStatusView(
            Id: id,
            DisplayName: displayName,
            Mode: "managed",
            Installed: "detected",
            InstallEvidence: [$"{evidenceNoun} found at {path}"],
            State: on ? "on" : "off",
            Receipt: "absent",
            Files: [new HarnessFileView(id, path, true, true, [key], on ? [key] : [], on ? [] : [key], [], null)],
            Warnings: [],
            ManualSteps: [],
            StatusLines: on ? ["OTLP export: enabled"] : ["OTLP export: disabled"],
            Health: on ? "ok" : "off",
            Issues: []);
    }

    private (FileProbe Probe, string Text) ProbeFile(string path)
    {
        if (!files.FileExists(path))
            return (FileProbe.Missing, string.Empty);

        try
        {
            return (FileProbe.Text, files.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (FileProbe.Unreadable, string.Empty);
        }
    }

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments that sit outside JSON strings.</summary>
    /// <param name="text">JSON or JSONC text.</param>
    /// <returns>Text a JSON parser can read.</returns>
    internal static string StripJsonComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder output = new(text.Length);
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            if (inString)
            {
                output.Append(current);
                if (escape)
                    escape = false;
                else if (current == '\\')
                    escape = true;
                else if (current == '"')
                    inString = false;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                output.Append(current);
                continue;
            }

            if (current == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;

                output.Append('\n');
                continue;
            }

            if (current == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;

                if (i + 1 < text.Length)
                    i++;
                continue;
            }

            output.Append(current);
        }

        return output.ToString();
    }

    private static bool? JsonBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool ClaudePointsAtLoopback(JsonElement root)
    {
        if (!root.TryGetProperty("env", out JsonElement env) || env.ValueKind != JsonValueKind.Object)
            return false;

        if (!env.TryGetProperty("OTEL_EXPORTER_OTLP_ENDPOINT", out JsonElement endpoint)
            || endpoint.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string value = endpoint.GetString() ?? string.Empty;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.IsLoopback
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private enum FileProbe
    {
        Missing,
        Unreadable,
        Text,
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
