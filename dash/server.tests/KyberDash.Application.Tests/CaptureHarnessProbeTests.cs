using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Harness capture probes read JSON, treat an unreadable file as unknown, and do not claim the
/// receiver is live just because the operator asked for it.
/// </summary>
public class CaptureHarnessProbeTests
{
    /// <summary>
    /// The Copilot probe reads the settings file VS Code actually writes on each platform. One
    /// test pins that layout so the probe cannot read a location VS Code never writes — which is
    /// exactly what happened when the probe resolved the macOS path and its tests seeded the
    /// Linux one.
    /// </summary>
    [Fact]
    public void CopilotSettingsFileFollowsTheVSCodeLayoutForThisPlatform()
    {
        string home = "/tmp/kw-capture-layout";

        string expected = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Code", "User", "settings.json")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json")
                : Path.Combine(home, ".config", "Code", "User", "settings.json");

        Assert.Equal(expected, CaptureService.CopilotSettingsFile(home));
    }

    [Fact]
    public void CopilotOnSurvivesWhitespaceAndIgnoresACommentedTrue()
    {
        string home = "/tmp/kw-capture-copilot";
        string path = CaptureService.CopilotSettingsFile(home);
        MemoryFiles files = new();
        files.Add(path, """
            {
              // "github.copilot.chat.otel.enabled": true
              "github.copilot.chat.otel.enabled":  true
            }
            """);
        CaptureService capture = Create(files, home);
        capture.ConfigureCapture(Json(new { receiverHosted = true }), "/tmp/kw-capture-state");

        JsonElement copilot = Harness(capture.GetCaptureStatus("/tmp/kw-capture-state", 4319), "copilot");

        Assert.Equal("on", copilot.GetProperty("state").GetString());
    }

    [Fact]
    public void CopilotOffWhenTheBooleanIsFalseDespiteANearbyMention()
    {
        string home = "/tmp/kw-capture-copilot-off";
        string path = CaptureService.CopilotSettingsFile(home);
        MemoryFiles files = new();
        files.Add(path, """{ "note": "github.copilot.chat.otel.enabled: true", "github.copilot.chat.otel.enabled": false }""");
        CaptureService capture = Create(files, home);

        JsonElement copilot = Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "copilot");

        Assert.Equal("off", copilot.GetProperty("state").GetString());
    }

    [Fact]
    public void UnreadableCopilotSettingsAreUnknownRatherThanDisabled()
    {
        string home = "/tmp/kw-capture-copilot-locked";
        string path = CaptureService.CopilotSettingsFile(home);
        MemoryFiles files = new();
        files.Unreadable(path);
        CaptureService capture = Create(files, home);

        JsonElement status = capture.GetCaptureStatus("/tmp/kw-capture-state");
        JsonElement copilot = Harness(status, "copilot");

        Assert.Equal("unknown", copilot.GetProperty("state").GetString());
        Assert.Equal("attention", copilot.GetProperty("health").GetString());
        Assert.Equal("attention", status.GetProperty("check").GetProperty("health").GetString());
        Assert.True(status.GetProperty("check").GetProperty("issueCount").GetInt32() > 0);
    }

    [Fact]
    public void MalformedCopilotSettingsAreUnknownRatherThanDisabled()
    {
        string home = "/tmp/kw-capture-copilot-bad-json";
        string path = CaptureService.CopilotSettingsFile(home);
        MemoryFiles files = new();
        files.Add(path, """{ "github.copilot.chat.otel.enabled": true""");
        CaptureService capture = Create(files, home);

        JsonElement copilot = Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "copilot");

        Assert.Equal("unknown", copilot.GetProperty("state").GetString());
        Assert.Equal("attention", copilot.GetProperty("health").GetString());
    }

    [Fact]
    public void ClaudeRequiresALoopbackEndpoint()
    {
        string home = "/tmp/kw-capture-claude";
        string path = Path.Combine(home, ".claude", "settings.json");
        MemoryFiles files = new();
        files.Add(path, """
            {
              "env": {
                "OTEL_LOGS_EXPORTER": "otlp",
                "OTEL_EXPORTER_OTLP_ENDPOINT": ""
              }
            }
            """);
        CaptureService capture = Create(files, home);

        Assert.Equal("off", Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "claude-code").GetProperty("state").GetString());

        files.Add(path, """{ "env": { "OTEL_EXPORTER_OTLP_ENDPOINT": "http://127.0.0.1:4319" } }""");
        Assert.Equal("on", Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "claude-code").GetProperty("state").GetString());

        files.Add(path, """{ "env": { "OTEL_EXPORTER_OTLP_ENDPOINT": "https://example.com" } }""");
        Assert.Equal("off", Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "claude-code").GetProperty("state").GetString());
    }

    [Fact]
    public void UnreadableClaudeSettingsAreUnknownRatherThanDisabled()
    {
        string home = "/tmp/kw-capture-claude-locked";
        MemoryFiles files = new();
        files.Unreadable(Path.Combine(home, ".claude", "settings.json"));
        CaptureService capture = Create(files, home);

        JsonElement claude = Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "claude-code");

        Assert.Equal("unknown", claude.GetProperty("state").GetString());
        Assert.Equal("attention", claude.GetProperty("health").GetString());
    }

    [Fact]
    public void OpenCodeEvidenceNamesThePluginWhenTheConfigFileIsAbsent()
    {
        string home = "/tmp/kw-capture-opencode";
        string plugin = Path.Combine(home, ".config", "opencode", "plugins", "kyberdash.ts");
        MemoryFiles files = new();
        files.Add(plugin, "export default {}");
        CaptureService capture = Create(files, home);

        JsonElement opencode = Harness(capture.GetCaptureStatus("/tmp/kw-capture-state"), "opencode");
        string evidence = opencode.GetProperty("installEvidence")[0].GetString() ?? string.Empty;

        Assert.Equal("on", opencode.GetProperty("state").GetString());
        Assert.Contains(plugin, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("config found", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexPiAndFactoryUseTheHarnessFileSeam()
    {
        string home = "/tmp/kw-capture-seam";
        MemoryFiles files = new();
        files.Add(Path.Combine(home, ".codex", "config.toml"), "model = \"gpt\"");
        files.Add(Path.Combine(home, ".pi", "agent", "settings.json"), "{}");
        files.AddDirectory(Path.Combine(home, ".factory"));
        CaptureService capture = Create(files, home);
        JsonElement status = capture.GetCaptureStatus("/tmp/kw-capture-state");

        Assert.Equal("detected", Harness(status, "codex").GetProperty("installed").GetString());
        Assert.Equal("detected", Harness(status, "pi").GetProperty("installed").GetString());
        Assert.Equal("detected", Harness(status, "factory").GetProperty("installed").GetString());
    }

    [Fact]
    public void ReceiverLivenessStaysUnknownAndTheDetailUsesTheGivenPort()
    {
        CaptureService capture = Create(new MemoryFiles(), "/tmp/kw-capture-none");
        capture.ConfigureCapture(Json(new { receiverHosted = true }), "/tmp/kw-capture-state");

        JsonElement status = capture.GetCaptureStatus("/tmp/kw-capture-state", 4319);

        Assert.Equal("unknown", status.GetProperty("receiver").GetProperty("liveness").GetString());
        Assert.Equal("http://127.0.0.1:4319", status.GetProperty("otlpEndpoint").GetString());
        string unconfigured = capture.GetCaptureStatus("/tmp/kw-capture-state").GetProperty("otlpEndpoint").GetString() ?? string.Empty;
        Assert.DoesNotContain("4318", unconfigured, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:4319", status.GetProperty("receiver").GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("4318", status.GetProperty("receiver").GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("is listening", status.GetProperty("receiver").GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HealthIsNotOkWhenAHarnessIsOnButTheReceiverIsNotHosted()
    {
        string home = "/tmp/kw-capture-health";
        string path = CaptureService.CopilotSettingsFile(home);
        MemoryFiles files = new();
        files.Add(path, """{ "github.copilot.chat.otel.enabled": true }""");
        CaptureService capture = Create(files, home);

        JsonElement check = capture.GetCaptureStatus("/tmp/kw-capture-state").GetProperty("check");

        Assert.NotEqual("ok", check.GetProperty("health").GetString());
    }

    private static CaptureService Create(MemoryFiles files, string home) =>
        new(new FakeSettingsRepository(), new InMemoryRelationalStore(), new FakeServerStateRepository(), files, () => home);

    private static JsonElement Harness(JsonElement status, string id)
    {
        foreach (JsonElement harness in status.GetProperty("harnesses").EnumerateArray())
        {
            if (harness.GetProperty("id").GetString() == id)
                return harness;
        }

        throw new InvalidOperationException($"Missing harness {id}");
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private sealed class MemoryFiles : IHarnessFileSystem
    {
        private readonly Dictionary<string, string?> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void Add(string path, string text) => files[path] = text;

        public void AddDirectory(string path) => directories.Add(path);

        public void Unreadable(string path) => files[path] = null;

        public bool FileExists(string path) => files.ContainsKey(path);

        public bool DirectoryExists(string path) => directories.Contains(path);

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive) => [];

        public IEnumerable<string> EnumerateDirectories(string directoryPath) => [];

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) =>
            files.TryGetValue(filePath, out string? text) && text is not null
                ? text
                : throw new UnauthorizedAccessException(filePath);

        public string GetFullPath(string path) => path;
    }
}
