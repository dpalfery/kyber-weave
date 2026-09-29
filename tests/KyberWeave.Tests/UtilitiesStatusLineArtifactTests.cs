using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract suite for the three Kyber-owned status-line artifacts the status-line slice imports
/// (development mode: test-first,
/// docs/archive/plans/2026-09-28-kyber-utilities-status-line-slice.md, Test contract row T4).
/// Pins each variant's declared segments, its runtime dependencies, its safety rules, and its
/// portability rules against synthetic payloads, before any file is imported.
/// </summary>
/// <remarks>
/// <para>
/// Authored before <c>products/kyber-utilities/statusline/</c> exists. Every case begins by
/// asserting its artifact is present, so the RED run fails on an assertion about the missing
/// artifact rather than a compile error or a fixture fault — which the plan's T4 row does not
/// accept as valid RED evidence.
/// </para>
/// <para>
/// The segment expectations are the literals the plan's "T0 outcome" inventory declares — the
/// Claude row's display tokens, the <c>agy</c> row's badge and five-section token breakdown, and
/// the Pi row's badge, <c>(in out cache)</c> breakdown, cost, and turn count — together with the
/// synthetic values this suite feeds in. They are not read back from the artifact, because a test
/// that asked the artifact what it renders and then asserted it rendered that would pass against
/// any script at all.
/// </para>
/// <para>
/// Fixtures under <c>tests/KyberWeave.Tests/Fixtures/statusline/</c> are hand-written from
/// published payload schemas and contain no real paths, conversation ids, or account data (C3).
/// The Claude and <c>agy</c> variants are run under their declared interpreters inside a temp
/// sandbox: a temp <c>HOME</c>, a <c>PATH</c> limited to stub directories and the directories of
/// the declared runtimes, a stub <c>git</c> that returns a synthetic branch and porcelain state,
/// network-denying stubs for <c>curl</c>/<c>wget</c>/<c>nc</c>, and a stub <c>kyberdash</c> for the
/// <c>agy</c> hand-off. The Pi variant runs through a <c>node --test</c> harness with a stub
/// extension context.
/// </para>
/// <para>
/// <b>Pinned artifact layout (contract this suite owns; T5 must satisfy it).</b>
/// <c>products/kyber-utilities/statusline/claude/statusline.sh</c>,
/// <c>products/kyber-utilities/statusline/antigravity/statusline.py</c>, and
/// <c>products/kyber-utilities/statusline/pi/statusbar.ts</c>. The plan names the
/// <c>{claude,antigravity,pi}</c> directories and the <c>statusbar.ts</c> file; it does not name
/// the two command files, so this suite fixes them.
/// </para>
/// <para>
/// <b>Pinned Pi extension contract (the plan delegates the stub-context shape to this suite).</b>
/// The extracted <c>statusbar.ts</c> exports an activation function — <c>activate(ctx)</c> or a
/// default export — and reads its data from <c>ctx</c>: <c>ctx.ui.mode</c> (<c>"tui"</c> or
/// <c>"plain"</c>), <c>ctx.ui.setStatus(text)</c>, <c>ctx.ui.setWidget(text)</c>, <c>ctx.cwd</c>,
/// <c>ctx.model</c>, <c>ctx.branch</c>, <c>ctx.tokens.{input,output,cache,total}</c>,
/// <c>ctx.costUsd</c>, and <c>ctx.turns</c>. The plan's T0 outcome Pi row owns the segments and the
/// <c>setStatus</c>/<c>setWidget</c> calls and records that this suite owns the stub-context shape
/// they are read from.
/// </para>
/// </remarks>
public sealed class UtilitiesStatusLineArtifactTests : IDisposable
{
    private const string StatusLineRootRelativePath = "products/kyber-utilities/statusline";
    private const string ClaudeArtifactRelativePath = StatusLineRootRelativePath + "/claude/statusline.sh";
    private const string AgyArtifactRelativePath = StatusLineRootRelativePath + "/antigravity/statusline.py";
    private const string PiArtifactRelativePath = StatusLineRootRelativePath + "/pi/statusbar.ts";

    private const string FixtureRootRelativePath = "tests/KyberWeave.Tests/Fixtures/statusline";
    private const string ClaudeFixture = "claude-statusline.json";
    private const string ClaudeZeroContextFixture = "claude-statusline-zero-context.json";
    private const string ClaudeWithoutContextFixture = "claude-statusline-without-context.json";
    private const string AgyFixture = "agy-statusline.json";
    private const string AgyWithoutUsageFixture = "agy-statusline-without-usage.json";
    private const string AgyWithoutModelFixture = "agy-statusline-without-model.json";
    private const string PiFixture = "pi-statusline.json";

    private const string SyntheticUser = "kyber-synthetic-user";
    private const string SyntheticHost = "kyber-synthetic-host";
    private const string SyntheticBranch = "kyber/synthetic-branch";
    private const string SyntheticPorcelainDirty = " M synthetic-modified.txt\n?? synthetic-untracked.txt\n";
    private const string SyntheticPorcelainClean = "";

    private const string CanaryVariable = "KYBER_STATUSLINE_CANARY";
    private const string CanaryValue = "kyber-canary-value-must-never-be-printed";

    private const string ClaudeModelDisplayName = "Kyber Synthetic Claude Model";
    private const string ClaudeOutputStyleName = "kyber-synthetic-style";
    private const string ClaudeWorkspaceName = "kyber-claude-workspace";
    private const string ClaudeWorkspacePath = "/synthetic/kyber-claude-workspace";

    private const string AgyModelDisplayName = "Kyber Synthetic Agy Model";
    private const string AgyWorkspaceName = "kyber-agy-workspace";

    private const string PiModelDisplayName = "Kyber Synthetic Pi Model";
    private const string PiWorkspaceName = "kyber-pi-workspace";

    private static readonly string[] ExpectedKyberdashArguments = ["kyber", "antigravity-statusline"];
    private static readonly string[] NetworkCommands = ["curl", "wget", "nc"];
    private static readonly string[] RuntimeCommands = ["bash", "sh", "jq", "awk", "python3", "node", "env"];

    /// <summary>Modules the extracted Pi extension must not import (T5's extraction boundary).</summary>
    private static readonly string[] ForbiddenCollectorImports =
        ["collector", "otlp", "opentelemetry", "exporter", "dev-link"];

    /// <summary>Source markers that would mean the extracted Pi extension opens a socket.</summary>
    private static readonly string[] ForbiddenNetworkMarkers =
    [
        "fetch(",
        "node:http",
        "node:https",
        "node:net",
        "node:dgram",
        "http.request",
        "https.request",
        "net.connect",
        "WebSocket"
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Claude: the bash variant and its declared segments.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ClaudeVariantRendersEverySegmentTheInventoryDeclares()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeFixture), false);

        AssertSucceeded(result, "Claude");
        string output = result.StandardOutput;
        AssertRendersValue(output, SyntheticUser + "@" + SyntheticHost, "user@host");
        AssertRendersValue(output, ClaudeWorkspaceName, "shortened directory");
        AssertRendersLabel(output, "git", "git branch");
        AssertRendersValue(output, SyntheticBranch, "git branch");
        AssertRendersLabel(output, "model", "model");
        AssertRendersValue(output, ClaudeModelDisplayName, "model");
        AssertRendersLabel(output, "output", "output style");
        AssertRendersValue(output, ClaudeOutputStyleName, "output style");
        AssertRendersLabel(output, "ctx", "context percentage");
        AssertRendersValue(output, "42", "context percentage");
        AssertRendersValue(output, "%", "context percentage");
        AssertOmits(
            output,
            ClaudeWorkspacePath,
            "the directory segment is declared shortened, so the full workspace path must not be rendered");
    }

    [Fact]
    public void ClaudeVariantReflectsGitPorcelainStateInItsRenderedOutput()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);
        string fixture = FixtureText(ClaudeFixture);

        sandbox.SetPorcelain(SyntheticPorcelainClean);
        string clean = RunVariant(sandbox, bash, artifact, fixture, false).StandardOutput;
        sandbox.SetPorcelain(SyntheticPorcelainDirty);
        string dirty = RunVariant(sandbox, bash, artifact, fixture, false).StandardOutput;

        Assert.NotEqual(clean, dirty);
    }

    [Fact]
    public void ClaudeVariantRendersAMissingContextPercentageAsAbsentRatherThanZero()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);

        string present = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeFixture), false).StandardOutput;
        string zero = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeZeroContextFixture), false).StandardOutput;
        string missing = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeWithoutContextFixture), false)
            .StandardOutput;

        Assert.NotEqual(present, missing);
        Assert.NotEqual(zero, missing);
    }

    [Theory]
    [InlineData("{\"model\": ")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ClaudeVariantExitsZeroAndRendersASafeFallbackOnMalformedJson(string standardInput)
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, bash, artifact, standardInput, false);

        AssertSucceeded(result, "Claude");
    }

    [Fact]
    public void ClaudeVariantWritesNoFileAnywhereInTheSandbox()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);
        string artifactDirectory = Path.GetDirectoryName(artifact) ?? string.Empty;
        Dictionary<string, string> before = Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory);

        ProcessResult result = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeFixture), false);

        AssertSucceeded(result, "Claude");
        AssertNoFilesChanged(before, Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory),
            "the Claude variant");
    }

    [Fact]
    public void ClaudeVariantMakesNoNetworkCallThroughTheSandboxStubs()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeFixture), false);

        AssertSucceeded(result, "Claude");
        Assert.False(
            File.Exists(sandbox.NetworkCalledFile),
            "The Claude variant invoked a network command (curl, wget, or nc); the import opens no socket (C8).");
    }

    [Fact]
    public void ClaudeVariantDoesNotEchoAnEnvironmentValueIntoItsOutput()
    {
        string artifact = RequireArtifact(ClaudeArtifactRelativePath);
        string bash = RequireRuntime("bash");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, bash, artifact, FixtureText(ClaudeFixture), false);

        AssertSucceeded(result, "Claude");
        Assert.DoesNotContain(CanaryValue, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryValue, result.StandardError, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // agy: the Python variant, its declared segments, and the KyberDash hand-off.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AgyVariantRendersEverySegmentTheInventoryDeclares()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false);

        AssertSucceeded(result, "agy");
        string output = result.StandardOutput;
        AssertRendersValue(output, "AGY", "AGY badge");
        AssertRendersValue(output, "📁", "repository directory");
        AssertRendersValue(output, AgyWorkspaceName, "repository directory");
        AssertRendersValue(output, "🌿", "git branch");
        AssertRendersValue(output, SyntheticBranch, "git branch");
        AssertRendersValue(output, "🤖", "model");
        AssertRendersValue(output, AgyModelDisplayName, "model");
        AssertRendersValue(output, "⚡", "token section");
        AssertRendersLabel(output, "sys", "five-part token breakdown");
        AssertRendersLabel(output, "tls", "five-part token breakdown");
        AssertRendersLabel(output, "skl", "five-part token breakdown");
        AssertRendersLabel(output, "rul", "five-part token breakdown");
        AssertRendersLabel(output, "msg", "five-part token breakdown");
        AssertRendersValue(output, "1024", "token total");
        AssertRendersValue(output, "⏳", "quota window");
        AssertRendersValue(output, "5h", "quota window");
        AssertRendersValue(output, "72", "quota percentage");
        AssertRendersValue(output, "%", "quota percentage");
        AssertRendersValue(output, "2h 30m", "quota reset duration");
        AssertRendersValue(output, "│", "section separators");
    }

    [Fact]
    public void AgyVariantRendersAMissingTokenBreakdownAsAbsentRatherThanZero()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        string present = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false).StandardOutput;
        string missing = RunVariant(sandbox, python, artifact, FixtureText(AgyWithoutUsageFixture), false)
            .StandardOutput;

        Assert.NotEqual(present, missing);
        AssertRendersValue(present, "1024", "token total");
        AssertOmits(missing, "1024", "an unreported token breakdown must not render a token count");
    }

    [Fact]
    public void AgyVariantRendersWithoutTheEmbeddedIdentityPromptFallback()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        string withModel = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false).StandardOutput;
        string withoutModel = RunVariant(sandbox, python, artifact, FixtureText(AgyWithoutModelFixture), false)
            .StandardOutput;

        AssertRendersValue(withModel, AgyModelDisplayName, "model");
        Assert.NotEqual(withModel, withoutModel);
        AssertOmits(withoutModel, AgyModelDisplayName,
            "a missing model must render as missing, not as a fabricated fallback");
    }

    [Theory]
    [InlineData("{\"conversation_id\": ")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AgyVariantExitsZeroAndRendersASafeFallbackOnMalformedJson(string standardInput)
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, python, artifact, standardInput, false);

        AssertSucceeded(result, "agy");
    }

    [Fact]
    public void AgyVariantWritesNoFileAnywhereInTheSandboxIncludingNoLastStdinDebugFile()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);
        string artifactDirectory = Path.GetDirectoryName(artifact) ?? string.Empty;
        Dictionary<string, string> before = Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory);

        ProcessResult result = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false);

        AssertSucceeded(result, "agy");
        AssertNoFilesChanged(before, Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory),
            "the agy variant");
    }

    [Fact]
    public void AgyVariantNeverConnectsToTheLocalCollectorPort()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);
        using TcpListener listener = new(IPAddress.Loopback, 4318);
        if (!TryStartCollectorListener(listener))
        {
            Assert.Skip("TCP port 4318 is already in use on this host, so the listener check cannot run.");
            return;
        }

        ProcessResult result = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false);

        AssertSucceeded(result, "agy");
        Assert.False(
            listener.Pending(),
            "The agy variant opened a connection to localhost:4318; the OTLP post is replaced by the KyberDash hand-off (D10).");
    }

    [Fact]
    public void AgyVariantDoesNotEchoAnEnvironmentValueIntoItsOutput()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false);

        AssertSucceeded(result, "agy");
        Assert.DoesNotContain(CanaryValue, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryValue, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void AgyVariantHandsItsStdinPayloadToKyberdashInTheBackground()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);
        string fixture = FixtureText(AgyFixture);

        ProcessResult result;
        try
        {
            result = RunVariant(sandbox, python, artifact, fixture, true, TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "The agy variant did not return while the kyberdash stub was still running, so the hand-off " +
                "is synchronous. C8 requires a background hand-off that cannot delay the status line.");
            return;
        }

        AssertSucceeded(result, "agy");
        Assert.True(
            WaitForFile(sandbox.KyberdashStartedFile, TimeSpan.FromSeconds(10)),
            "The agy variant never invoked the stub kyberdash, so it does not hand its payload off (D10).");
        Assert.False(
            File.Exists(sandbox.KyberdashFinishedFile),
            "The agy variant waited for the kyberdash hand-off to finish; it must return first (C8).");

        Assert.Equal(ExpectedKyberdashArguments, File.ReadAllLines(sandbox.KyberdashArgvFile));
        Assert.Equal(fixture, File.ReadAllText(sandbox.KyberdashStdinFile));

        ProcessResult withoutKyberdash = RunVariant(sandbox, python, artifact, fixture, false);
        AssertSucceeded(withoutKyberdash, "agy");
        Assert.Equal(withoutKyberdash.StandardOutput, result.StandardOutput);

        File.WriteAllText(sandbox.KyberdashReleaseFile, string.Empty);
        Assert.True(
            WaitForFile(sandbox.KyberdashFinishedFile, TimeSpan.FromSeconds(30)),
            "The stub kyberdash did not finish after the release file appeared.");
    }

    [Fact]
    public void AgyVariantStillRendersAndExitsZeroWhenKyberdashIsNotOnPath()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        ProcessResult result = RunVariant(sandbox, python, artifact, FixtureText(AgyFixture), false);

        AssertSucceeded(result, "agy");
        AssertRendersValue(result.StandardOutput, "AGY", "AGY badge");
        AssertRendersValue(result.StandardOutput, SyntheticBranch, "git branch");
    }

    // -----------------------------------------------------------------------------------------
    // Pi: the extracted extension, run through a node --test harness with a stub context.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void PiExtensionRendersEverySegmentTheInventoryDeclaresInTuiMode()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "pi-report-tui.json");

        ProcessResult result = RunPiHarness(sandbox, node, artifact, reportPath, "tui");

        AssertSucceeded(result, "Pi");
        string report = File.ReadAllText(reportPath);
        string[] status = ReportStrings(report, "setStatus");
        string[] widget = ReportStrings(report, "setWidget");
        string rendered = string.Join("\n", status);
        Assert.True(status.Length > 0, "The Pi extension recorded no ctx.ui.setStatus call in TUI mode.");
        Assert.True(widget.Length > 0, "The Pi extension recorded no ctx.ui.setWidget call in TUI mode.");
        AssertRendersValue(rendered, "PI", "PI badge");
        AssertRendersValue(rendered, "📁", "repository name");
        AssertRendersValue(rendered, PiWorkspaceName, "repository name");
        AssertRendersValue(rendered, "🌿", "git branch");
        AssertRendersValue(rendered, SyntheticBranch, "git branch");
        AssertRendersValue(rendered, "🤖", "model");
        AssertRendersValue(rendered, PiModelDisplayName, "model");
        AssertRendersValue(rendered, "⚡", "token section");
        AssertRendersLabel(rendered, "in:", "token breakdown");
        AssertRendersLabel(rendered, "out:", "token breakdown");
        AssertRendersLabel(rendered, "cache:", "token breakdown");
        AssertRendersValue(rendered, "742", "token total");
        AssertRendersValue(rendered, "💰", "cost");
        AssertRendersValue(rendered, "1.23", "cost");
        AssertRendersValue(rendered, "⏳", "turn count");
        AssertRendersValue(rendered, "7", "turn count");
        AssertRendersValue(rendered, "│", "compact footer separators");
    }

    [Fact]
    public void PiExtensionWritesItsStatusToStandardErrorOutsideTuiMode()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "pi-report-plain.json");

        ProcessResult result = RunPiHarness(sandbox, node, artifact, reportPath, "plain");

        AssertSucceeded(result, "Pi");
        string report = File.ReadAllText(reportPath);
        string[] status = ReportStrings(report, "setStatus");
        string[] stderr = ReportStrings(report, "stderr");
        Assert.True(status.Length == 0, "The Pi extension used ctx.ui.setStatus outside TUI mode.");
        string rendered = string.Join("\n", stderr);
        Assert.True(stderr.Length > 0, "The Pi extension wrote nothing to stderr outside TUI mode.");
        AssertRendersValue(rendered, "PI", "PI badge");
        AssertRendersValue(rendered, PiModelDisplayName, "model");
    }

    [Fact]
    public void PiExtensionImportsNothingFromTheCollectorPackage()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string[] modules = PiExtensionModules(artifact);
        Assert.True(modules.Length > 0, "The extracted Pi extension has no TypeScript module.");
        foreach (string module in modules)
        {
            string source = File.ReadAllText(module);
            foreach (string forbidden in ForbiddenCollectorImports)
            {
                Assert.False(
                    source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"'{module}' references the collector package ('{forbidden}'); T5 extracts only the status-bar module.");
            }
        }
    }

    [Fact]
    public void PiExtensionMakesNoNetworkCall()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string[] modules = PiExtensionModules(artifact);
        Assert.True(modules.Length > 0, "The extracted Pi extension has no TypeScript module.");
        foreach (string module in modules)
        {
            string source = File.ReadAllText(module);
            foreach (string marker in ForbiddenNetworkMarkers)
            {
                Assert.False(
                    source.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    $"'{module}' contains a network call marker ('{marker}'); the import opens no socket (C8).");
            }
        }
    }

    [Fact]
    public void PiExtensionWritesNoFileAnywhereInTheSandbox()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string artifactDirectory = Path.GetDirectoryName(artifact) ?? string.Empty;
        Dictionary<string, string> before = Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory);

        ProcessResult result = RunPiHarness(sandbox, node, artifact,
            Path.Combine(sandbox.RecordDirectory, "pi-report-write.json"), "tui");

        AssertSucceeded(result, "Pi");
        AssertNoFilesChanged(before, Snapshot(sandbox.HomeDirectory, sandbox.WorkDirectory, artifactDirectory),
            "the Pi extension");
    }

    [Fact]
    public void PiExtensionDoesNotEchoAnEnvironmentValueIntoItsOutput()
    {
        string artifact = RequireArtifact(PiArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "pi-report-canary.json");

        ProcessResult result = RunPiHarness(sandbox, node, artifact, reportPath, "tui");

        AssertSucceeded(result, "Pi");
        string rendered = string.Join("\n", ReportStrings(File.ReadAllText(reportPath), "setStatus"))
                          + "\n"
                          + string.Join("\n", ReportStrings(File.ReadAllText(reportPath), "stderr"));
        Assert.DoesNotContain(CanaryValue, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryValue, result.StandardError, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Portability and privacy: the static rules every artifact must satisfy (C6).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void NoArtifactCarriesAnAbsoluteHomePathOrPrivateEndpointLiteral()
    {
        string[] artifacts =
        [
            RequireArtifact(ClaudeArtifactRelativePath),
            RequireArtifact(AgyArtifactRelativePath),
            RequireArtifact(PiArtifactRelativePath)
        ];
        foreach (string artifact in artifacts)
        {
            string source = File.ReadAllText(artifact);
            Assert.False(source.Contains("/Users/", StringComparison.Ordinal),
                $"'{artifact}' contains a macOS absolute home path.");
            Assert.False(source.Contains("/home/", StringComparison.Ordinal),
                $"'{artifact}' contains a Linux absolute home path.");
            Assert.False(source.Contains("localhost", StringComparison.OrdinalIgnoreCase),
                $"'{artifact}' names localhost.");
            Assert.False(source.Contains("4318", StringComparison.Ordinal),
                $"'{artifact}' names the local collector port 4318.");
            Assert.False(source.Contains("otlp", StringComparison.OrdinalIgnoreCase), $"'{artifact}' names OTLP.");
            Assert.False(source.Contains("opentelemetry", StringComparison.OrdinalIgnoreCase),
                $"'{artifact}' names OpenTelemetry.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: artifacts, fixtures, and runtimes.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts the artifact exists and returns its absolute path. The RED run fails here, on an
    /// assertion about the missing artifact, which is the evidence the plan's T4 row accepts.
    /// </summary>
    private static string RequireArtifact(string relativePath)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Kyber Utilities status-line artifacts are macOS and Linux only (D4).");

        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, ToNative(relativePath));
        Assert.True(
            File.Exists(path),
            $"The Kyber-owned artifact '{path}' does not exist yet. T4 is the RED suite for T5's " +
            "import, and it fails on this assertion until the artifact lands.");
        return path;
    }

    private static string RequireRuntime(string command)
    {
        string path = FindOnPath(command) ?? string.Empty;
        if (path.Length == 0)
            Assert.Skip($"The '{command}' runtime is not available, so this artifact case is skipped.");
        return path;
    }

    private static string? FindOnPath(string command)
    {
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string FixtureText(string fixtureFileName)
    {
        string path = FixturePath(fixtureFileName);
        Assert.True(
            File.Exists(path),
            $"The synthetic fixture '{path}' is missing; the fixture set is part of this suite, so its " +
            "absence is a test fault, not the artifact under test.");
        return File.ReadAllText(path);
    }

    private static string FixturePath(string fixtureFileName)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, ToNative(FixtureRootRelativePath), fixtureFileName);
    }

    private static string[] PiExtensionModules(string artifact)
    {
        string directory = Path.GetDirectoryName(artifact) ?? string.Empty;
        return
        [
            .. EnumerateFilesSafely(directory)
                .Where(file =>
                    file.EndsWith(".ts", StringComparison.Ordinal) ||
                    file.EndsWith(".mts", StringComparison.Ordinal) ||
                    file.EndsWith(".js", StringComparison.Ordinal) ||
                    file.EndsWith(".mjs", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: running the variants inside the sandbox.
    // -----------------------------------------------------------------------------------------

    private static ProcessResult RunVariant(
        Sandbox sandbox,
        string interpreter,
        string artifactPath,
        string standardInput,
        bool withKyberdash,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new(interpreter)
        {
            WorkingDirectory = sandbox.WorkDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(artifactPath);
        sandbox.ConfigureEnvironment(startInfo, withKyberdash);
        return ProcessRunner.Run(startInfo, standardInput, timeout ?? TimeSpan.FromSeconds(30));
    }

    private static ProcessResult RunPiHarness(
        Sandbox sandbox,
        string node,
        string artifactPath,
        string reportPath,
        string mode)
    {
        ProcessStartInfo startInfo = new(node)
        {
            WorkingDirectory = sandbox.WorkDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--test");
        startInfo.ArgumentList.Add(sandbox.HarnessPath);
        sandbox.ConfigureEnvironment(startInfo, false);
        startInfo.Environment["PI_ARTIFACT"] = artifactPath;
        startInfo.Environment["PI_FIXTURE"] = FixturePath(PiFixture);
        startInfo.Environment["PI_REPORT"] = reportPath;
        startInfo.Environment["PI_MODE"] = mode;
        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: assertions.
    // -----------------------------------------------------------------------------------------

    private static void AssertSucceeded(ProcessResult result, string variant)
    {
        Assert.True(
            result.ExitCode == 0,
            $"The {variant} artifact exited {result.ExitCode} instead of 0.\n" +
            $"stdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
    }

    private static void AssertRendersValue(string output, string value, string segment)
    {
        Assert.True(
            output.Contains(value, StringComparison.Ordinal),
            $"The {segment} segment is missing from the rendered status line: no '{value}' in:\n{output}");
    }

    private static void AssertRendersLabel(string output, string label, string segment)
    {
        Assert.True(
            output.Contains(label, StringComparison.OrdinalIgnoreCase),
            $"The {segment} segment is missing from the rendered status line: no '{label}' in:\n{output}");
    }

    private static void AssertOmits(string output, string value, string because)
    {
        Assert.False(
            output.Contains(value, StringComparison.Ordinal),
            $"The rendered status line contains '{value}', but {because}:\n{output}");
    }

    private static string[] ReportStrings(string reportJson, string property)
    {
        using JsonDocument document = JsonDocument.Parse(reportJson);
        if (!document.RootElement.TryGetProperty(property, out JsonElement element))
            return [];
        return [.. element.EnumerateArray().Select(item => item.GetString() ?? string.Empty)];
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: the filesystem sandbox and the write watch.
    // -----------------------------------------------------------------------------------------

    private static Dictionary<string, string> Snapshot(params string[] roots)
    {
        Dictionary<string, string> snapshot = new(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            foreach (string file in EnumerateFilesSafely(root))
                snapshot[file] = Digest(File.ReadAllBytes(file));
        }

        return snapshot;
    }

    private static void AssertNoFilesChanged(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        string subject)
    {
        string[] added = [.. after.Keys.Where(key => !before.ContainsKey(key)).Order(StringComparer.Ordinal)];
        string[] removed = [.. before.Keys.Where(key => !after.ContainsKey(key)).Order(StringComparer.Ordinal)];
        string[] changed =
        [
            .. before.Keys
                .Where(key => after.TryGetValue(key, out string? digest) &&
                              !string.Equals(digest, before[key], StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
        Assert.True(
            added.Length == 0 && removed.Length == 0 && changed.Length == 0,
            $"{subject} wrote to the sandbox: added [{Join(added)}], removed [{Join(removed)}], " +
            $"changed [{Join(changed)}].");
    }

    /// <summary>
    /// Recursively enumerates files without following symlinks, matching the containment rule in
    /// the C# coding standard rather than <c>SearchOption.AllDirectories</c>.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            if (new FileInfo(file).LinkTarget is null)
                yield return file;
        }

        foreach (string directory in Directory.EnumerateDirectories(root, "*", options))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
                continue;
            foreach (string file in EnumerateFilesSafely(directory))
                yield return file;
        }
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        if (File.Exists(path))
            return true;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length == 0 || !Directory.Exists(directory))
            return false;

        using FileSystemWatcher watcher = new(directory, Path.GetFileName(path));
        using ManualResetEventSlim signal = new(false);
        watcher.Created += (_, _) => signal.Set();
        watcher.EnableRaisingEvents = true;
        return signal.Wait(timeout) || File.Exists(path);
    }

    private static bool TryStartCollectorListener(TcpListener listener)
    {
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string ToNative(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static string Join(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(value => $"'{value}'"));
    }

    // -----------------------------------------------------------------------------------------
    // The sandbox: temp HOME, limited PATH, stub git, network-denying stubs, stub kyberdash.
    // -----------------------------------------------------------------------------------------

    private sealed class Sandbox
    {
        public Sandbox(TempDirectory temp)
        {
            HomeDirectory = Path.Combine(temp.Path, "home");
            WorkDirectory = Path.Combine(temp.Path, "work");
            StubDirectory = Path.Combine(temp.Path, "bin");
            KyberdashDirectory = Path.Combine(temp.Path, "bin-kyberdash");
            RecordDirectory = Path.Combine(temp.Path, "recordings");
            Directory.CreateDirectory(HomeDirectory);
            Directory.CreateDirectory(WorkDirectory);
            Directory.CreateDirectory(StubDirectory);
            Directory.CreateDirectory(KyberdashDirectory);
            Directory.CreateDirectory(RecordDirectory);

            GitBranchFile = Path.Combine(RecordDirectory, "git-branch");
            GitPorcelainFile = Path.Combine(RecordDirectory, "git-porcelain");
            NetworkCalledFile = Path.Combine(RecordDirectory, "network-called");
            KyberdashArgvFile = Path.Combine(RecordDirectory, "kyberdash-argv");
            KyberdashStdinFile = Path.Combine(RecordDirectory, "kyberdash-stdin");
            KyberdashStartedFile = Path.Combine(RecordDirectory, "kyberdash-started");
            KyberdashFinishedFile = Path.Combine(RecordDirectory, "kyberdash-finished");
            KyberdashReleaseFile = Path.Combine(RecordDirectory, "kyberdash-release");
            HarnessPath = Path.Combine(temp.Path, "pi-harness.mjs");

            File.WriteAllText(GitBranchFile, SyntheticBranch + "\n");
            File.WriteAllText(GitPorcelainFile, SyntheticPorcelainDirty);
            File.WriteAllText(HarnessPath, PiHarnessScript);

            WriteStub(Path.Combine(StubDirectory, "git"), GitStubScript);
            WriteStub(Path.Combine(StubDirectory, "hostname"), HostnameStubScript);
            WriteStub(Path.Combine(StubDirectory, "whoami"), WhoamiStubScript);
            WriteStub(Path.Combine(StubDirectory, "id"), IdStubScript);
            foreach (string networkCommand in NetworkCommands)
                WriteStub(Path.Combine(StubDirectory, networkCommand), NetworkStubScript);
            WriteStub(Path.Combine(KyberdashDirectory, "kyberdash"), KyberdashStubScript);

            RuntimeDirectories = ResolveRuntimeDirectories();
        }

        public string HomeDirectory { get; }

        public string WorkDirectory { get; }

        public string StubDirectory { get; }

        public string KyberdashDirectory { get; }

        public string RecordDirectory { get; }

        public string GitBranchFile { get; }

        public string GitPorcelainFile { get; }

        public string NetworkCalledFile { get; }

        public string KyberdashArgvFile { get; }

        public string KyberdashStdinFile { get; }

        public string KyberdashStartedFile { get; }

        public string KyberdashFinishedFile { get; }

        public string KyberdashReleaseFile { get; }

        public string HarnessPath { get; }

        public IReadOnlyList<string> RuntimeDirectories { get; }

        public void SetPorcelain(string porcelain)
        {
            File.WriteAllText(GitPorcelainFile, porcelain);
        }

        /// <summary>
        /// Replaces the child environment outright. <c>ProcessRunner.Run</c> copies exactly what the
        /// start info carries, so clearing here is what keeps the operator's real environment — and
        /// the operator's real <c>PATH</c> — out of the artifact under test.
        /// </summary>
        public void ConfigureEnvironment(ProcessStartInfo startInfo, bool withKyberdash)
        {
            startInfo.Environment.Clear();
            startInfo.Environment["PATH"] = PathValue(withKyberdash);
            startInfo.Environment["HOME"] = HomeDirectory;
            startInfo.Environment["USER"] = SyntheticUser;
            startInfo.Environment["LOGNAME"] = SyntheticUser;
            startInfo.Environment["HOSTNAME"] = SyntheticHost;
            startInfo.Environment["SHELL"] = "/bin/sh";
            startInfo.Environment["TERM"] = "dumb";
            startInfo.Environment[CanaryVariable] = CanaryValue;
            startInfo.Environment["STUB_GIT_BRANCH_FILE"] = GitBranchFile;
            startInfo.Environment["STUB_GIT_PORCELAIN_FILE"] = GitPorcelainFile;
            startInfo.Environment["KYBER_STUB_HOST"] = SyntheticHost;
            startInfo.Environment["KYBER_STUB_USER"] = SyntheticUser;
            startInfo.Environment["KYBER_STUB_RECORD_DIR"] = RecordDirectory;
            startInfo.Environment["KYBER_STUB_RELEASE_FILE"] = KyberdashReleaseFile;
            // Apple framework python3 writes stdlib .pyc caches under HOME at interpreter startup,
            // before the artifact runs — HOME is snapshotted, so redirect bytecode outside it.
            startInfo.Environment["PYTHONPYCACHEPREFIX"] =
                Path.Combine(Path.GetTempPath(), "kw-statusline-pycache");
        }

        private string PathValue(bool withKyberdash)
        {
            List<string> entries = [];
            if (withKyberdash)
                entries.Add(KyberdashDirectory);
            entries.Add(StubDirectory);
            entries.AddRange(RuntimeDirectories);
            return string.Join(Path.PathSeparator, entries);
        }

        private static void WriteStub(string path, string content)
        {
            File.WriteAllText(path, content);
            if (OperatingSystem.IsWindows())
                return;
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        private static IReadOnlyList<string> ResolveRuntimeDirectories()
        {
            List<string> directories = [];
            foreach (string command in RuntimeCommands)
            {
                string? resolved = FindOnPath(command);
                string? directory = resolved is null ? null : Path.GetDirectoryName(resolved);
                if (directory is null || directories.Contains(directory, StringComparer.Ordinal))
                    continue;
                directories.Add(directory);
            }

            return directories;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Stub scripts and the Pi node --test harness.
    // -----------------------------------------------------------------------------------------

    private const string GitStubScript = """
                                         #!/bin/sh
                                         for argument in "$@"; do
                                           case "$argument" in
                                             status)
                                               cat "$STUB_GIT_PORCELAIN_FILE"
                                               exit 0
                                               ;;
                                             rev-parse|branch|symbolic-ref|describe)
                                               cat "$STUB_GIT_BRANCH_FILE"
                                               exit 0
                                               ;;
                                           esac
                                         done
                                         cat "$STUB_GIT_BRANCH_FILE"
                                         exit 0
                                         """;

    private const string NetworkStubScript = """
                                             #!/bin/sh
                                             : > "$KYBER_STUB_RECORD_DIR/network-called"
                                             exit 1
                                             """;

    private const string HostnameStubScript = """
                                              #!/bin/sh
                                              printf '%s\n' "$KYBER_STUB_HOST"
                                              """;

    private const string WhoamiStubScript = """
                                            #!/bin/sh
                                            printf '%s\n' "$KYBER_STUB_USER"
                                            """;

    private const string IdStubScript = """
                                        #!/bin/sh
                                        case "$1" in
                                          -un)
                                            printf '%s\n' "$KYBER_STUB_USER"
                                            ;;
                                          *)
                                            printf 'uid=501(%s) gid=20(staff) groups=20(staff)\n' "$KYBER_STUB_USER"
                                            ;;
                                        esac
                                        """;

    /// <summary>
    /// Records its argv and stdin, then blocks on a release file. The hand-off case proves the
    /// variant returned first by checking the "finished" marker is absent while this stub is still
    /// running, so the background hand-off is proven without a sleep in the test itself.
    /// </summary>
    private const string KyberdashStubScript = """
                                               #!/bin/sh
                                               record="$KYBER_STUB_RECORD_DIR"
                                               printf '%s\n' "$@" > "$record/kyberdash-argv"
                                               cat > "$record/kyberdash-stdin"
                                               : > "$record/kyberdash-started"
                                               attempts=0
                                               while [ ! -f "$KYBER_STUB_RELEASE_FILE" ] && [ "$attempts" -lt 1200 ]; do
                                                 sleep 0.1
                                                 attempts=$((attempts + 1))
                                               done
                                               : > "$record/kyberdash-finished"
                                               exit 0
                                               """;

    /// <summary>
    /// The stub extension context the Pi variant is driven through. It records
    /// <c>ctx.ui.setStatus</c> and <c>ctx.ui.setWidget</c> calls and captures stderr, and writes a
    /// JSON report the C# suite asserts against.
    /// </summary>
    private const string PiHarnessScript = """
                                           import { after, test } from 'node:test'
                                           import assert from 'node:assert/strict'
                                           import { readFileSync, writeFileSync } from 'node:fs'
                                           import { pathToFileURL } from 'node:url'

                                           const report = { setStatus: [], setWidget: [], stderr: [], error: null }

                                           const fixture = JSON.parse(readFileSync(process.env.PI_FIXTURE, 'utf8'))

                                           const ui = {
                                             mode: process.env.PI_MODE,
                                             setStatus: (text) => { report.setStatus.push(String(text)) },
                                             setWidget: (text) => { report.setWidget.push(String(text)) },
                                           }

                                           const context = {
                                             ui,
                                             cwd: fixture.cwd,
                                             model: fixture.model,
                                             branch: fixture.branch,
                                             tokens: fixture.tokens,
                                             costUsd: fixture.costUsd,
                                             turns: fixture.turns,
                                           }

                                           process.stderr.write = (chunk) => { report.stderr.push(String(chunk)); return true }

                                           after(() => {
                                             writeFileSync(process.env.PI_REPORT, JSON.stringify(report))
                                           })

                                           test('the extracted extension activates against the stub context', async () => {
                                             const module = await import(pathToFileURL(process.env.PI_ARTIFACT).href)
                                             const activate = typeof module.activate === 'function' ? module.activate : module.default
                                             assert.equal(typeof activate, 'function', 'the extension must export activate() or a default function')
                                             await activate(context)
                                           })
                                           """;
}
