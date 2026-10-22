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
    private const string CommandCodeArtifactRelativePath =
        StatusLineRootRelativePath + "/commandcode/statusbar.ts";
    private const string CommandCodeHarnessFileName = "commandcode-harness.mjs";

    private const string FixtureRootRelativePath = "tests/KyberWeave.Tests/Fixtures/statusline";
    private const string ClaudeFixture = "claude-statusline.json";
    private const string ClaudeZeroContextFixture = "claude-statusline-zero-context.json";
    private const string ClaudeWithoutContextFixture = "claude-statusline-without-context.json";
    private const string AgyFixture = "agy-statusline.json";
    private const string AgyWithoutUsageFixture = "agy-statusline-without-usage.json";
    private const string AgyWithoutModelFixture = "agy-statusline-without-model.json";
    private const string PiFixture = "pi-statusline.json";
    private const string CommandCodeFixture = "commandcode-statusline.json";

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
    private const string CommandCodeModelDisplayName = "Kyber Synthetic CommandCode Model";
    private const string CommandCodeWorkspaceName = "kyber-commandcode-workspace";

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

    /// <summary>
    /// The agy variant must open no socket at all, observed on its own process through
    /// the audit probe. A previous revision bound host-global port 4318 and blamed any
    /// loopback connection on the artifact (red reproduction: synthetic empty connects
    /// false-failed it in ~256ms while the artifact opened nothing).
    /// </summary>
    [Fact]
    public async Task AgyVariantNeverConnectsToTheLocalCollectorPort()
    {
        string artifact = RequireArtifact(AgyArtifactRelativePath);
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);

        // Unrelated host-side traffic on a private listener, concurrent with the run.
        // Only that task dials it, so it can never be mistaken for the artifact — and
        // the artifact's audit log can never observe it.
        Task<int> unrelated = Task.Run(EmitAndAcceptUnrelatedConnections);

        ProcessResult result;
        int completed;
        try
        {
            result = RunWithSocketAudit(sandbox, python, artifact, FixtureText(AgyFixture));
        }
        finally
        {
            // Joined on every path, so no background task survives a failure either.
            completed = await unrelated.WaitAsync(TimeSpan.FromSeconds(30));
        }

        AssertSucceeded(result, "agy");
        Assert.True(
            completed > 0,
            "The unrelated-traffic fixture completed no loopback connection, so it proved nothing.");
        AssertNoSocketConnects(sandbox.SocketAuditLogFile, "the agy variant");
    }

    /// <summary>
    /// Negative control: a script that deliberately attempts the collector port must be
    /// recorded, whether the port refuses or a real collector accepts. No payload is sent.
    /// </summary>
    [Fact]
    public void SocketAuditProbeRecordsADeliberateCollectorConnectionAttempt()
    {
        string python = RequireRuntime("python3");
        Sandbox sandbox = new(_temp);
        string fixture = Path.Combine(sandbox.WorkDirectory, "deliberate-collector-connect.py");
        File.WriteAllText(fixture, DeliberateCollectorConnectScript);

        ProcessResult result = RunWithSocketAudit(sandbox, python, fixture, string.Empty);

        AssertSucceeded(result, "audit probe");
        string audit = File.Exists(sandbox.SocketAuditLogFile)
            ? File.ReadAllText(sandbox.SocketAuditLogFile)
            : string.Empty;
        Assert.Contains(
            "4318",
            audit,
            StringComparison.Ordinal);
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
    // CommandCode: the mod, run through a node --test harness with a stub ModApi.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CommandCodeModRendersEverySegmentTheInventoryDeclaresInTuiMode()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-tui.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "tui");

        AssertSucceeded(result, "CommandCode");
        string report = File.ReadAllText(reportPath);
        string[] status = ReportStrings(report, "setStatus");
        string rendered = string.Join("\n", status);
        Assert.True(status.Length > 0, "The CommandCode mod recorded no cmd.ui.setStatus call in TUI mode.");
        AssertRendersValue(rendered, "CC", "CC badge");
        AssertRendersValue(rendered, "📁", "repository name");
        AssertRendersValue(rendered, CommandCodeWorkspaceName, "repository name");
        AssertRendersValue(rendered, "🌿", "git branch");
        AssertRendersValue(rendered, SyntheticBranch, "git branch");
        AssertRendersValue(rendered, "🤖", "model");
        AssertRendersValue(rendered, CommandCodeModelDisplayName, "model");
        AssertRendersValue(rendered, "⚡", "token section");
        AssertRendersLabel(rendered, "in:", "token breakdown");
        AssertRendersLabel(rendered, "out:", "token breakdown");
        AssertRendersValue(rendered, "742", "token total");
        AssertRendersValue(rendered, "⏳", "turn count");
        AssertRendersValue(rendered, "3", "turn count");
        AssertRendersValue(rendered, "│", "compact footer separators");
    }

    [Fact]
    public void CommandCodeModClearsItsSegmentOutsideTuiMode()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-plain.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "plain");

        AssertSucceeded(result, "CommandCode");
        string report = File.ReadAllText(reportPath);
        string[] status = ReportStrings(report, "setStatus");
        Assert.True(status.Length > 0, "The CommandCode mod recorded no cmd.ui.setStatus call outside TUI mode.");
        Assert.True(status.All(text => string.IsNullOrWhiteSpace(text)),
            "The CommandCode mod must clear its status segment outside TUI mode, so a stale bar never " +
            "survives a headless run:\n" + string.Join("\n", status));
    }

    [Fact]
    public void CommandCodeModOmitsTheCostSegmentBecauseNoCostApiExists()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string source = File.ReadAllText(artifact);
        Assert.DoesNotContain("💰", source, StringComparison.Ordinal);
        Assert.DoesNotContain("costUsd", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totalCost", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCodeModMakesNoNetworkCallAndWritesNoFile()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        foreach (string marker in ForbiddenNetworkMarkers)
        {
            Assert.False(
                File.ReadAllText(artifact).Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"The CommandCode mod contains a network call marker ('{marker}'); the import opens no socket (C8).");
        }

        string[] modules = [artifact];
        Assert.True(modules.Length > 0, "The CommandCode mod has no TypeScript module.");
        foreach (string module in modules)
        {
            Assert.False(
                File.ReadAllText(module).Contains("fs.write", StringComparison.OrdinalIgnoreCase),
                $"The mod writes through the fs module; the status line must write no file.");
        }
    }

    [Fact]
    public void CommandCodeModDoesNotEchoAnEnvironmentValueIntoItsOutput()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-canary.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "tui");

        AssertSucceeded(result, "CommandCode");
        string rendered = string.Join("\n", ReportStrings(File.ReadAllText(reportPath), "setStatus"));
        Assert.DoesNotContain(CanaryValue, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryValue, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandCodeModRendersNonFiniteNumbersAsAbsentAndKeepsTheLastGoodTurn()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-poison.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "tui", "CC_POISON_TURN=1");

        AssertSucceeded(result, "CommandCode");
        string[] status = ReportStrings(File.ReadAllText(reportPath), "setStatus");
        Assert.True(status.Length > 0, "The CommandCode mod recorded no cmd.ui.setStatus call in TUI mode.");
        string rendered = string.Join("\n", status);
        Assert.DoesNotContain("NaN", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", rendered, StringComparison.Ordinal);
        AssertRendersValue(rendered, "⏳", "turn count");
        AssertRendersValue(rendered, "turn #3", "the last good turn survives a non-finite turn number");
    }

    [Fact]
    public void CommandCodeModClearsItsSegmentWhenTheHostOmitsCapabilities()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-nocaps.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "tui", "CC_NO_CAPS=1");

        AssertSucceeded(result, "CommandCode");
        string[] status = ReportStrings(File.ReadAllText(reportPath), "setStatus");
        Assert.True(status.Length > 0, "The CommandCode mod recorded no cmd.ui.setStatus call without capabilities.");
        Assert.True(status.All(text => string.IsNullOrWhiteSpace(text)),
            "The CommandCode mod must clear its segment when the host omits capabilities, so a stale bar " +
            "never survives:\n" + string.Join("\n", status));
    }

    [Fact]
    public void CommandCodeModSurvivesHookRegistrationFailure()
    {
        string artifact = RequireArtifact(CommandCodeArtifactRelativePath);
        string node = RequireRuntime("node");
        Sandbox sandbox = new(_temp);
        string reportPath = Path.Combine(sandbox.RecordDirectory, "commandcode-report-hooks-throw.json");

        ProcessResult result = RunCommandCodeHarness(sandbox, node, artifact, reportPath, "tui", "CC_HOOKS_THROW=1");

        // A host that rejects hook registration must not take the session down: the node test
        // asserting registration would throw on a factory failure, so a zero exit is the evidence.
        AssertSucceeded(result, "CommandCode");
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
            RequireArtifact(PiArtifactRelativePath),
            RequireArtifact(CommandCodeArtifactRelativePath)
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
        ProcessStartInfo startInfo = VariantStartInfo(sandbox, interpreter, artifactPath, withKyberdash);
        return ProcessRunner.Run(startInfo, standardInput, timeout ?? TimeSpan.FromSeconds(30));
    }

    private static ProcessStartInfo VariantStartInfo(
        Sandbox sandbox,
        string interpreter,
        string artifactPath,
        bool withKyberdash)
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
        return startInfo;
    }

    /// <summary>
    /// Runs a Python script with the child-scoped socket-connect audit installed: a
    /// <c>sitecustomize</c> probe, found through a <c>PYTHONPATH</c> that exists only in
    /// the child's environment, records every <c>socket.connect</c> attempt to the audit
    /// log. Fails the run when the probe itself did not initialize — an empty log must
    /// mean no connects, never a broken hook.
    /// </summary>
    private static ProcessResult RunWithSocketAudit(
        Sandbox sandbox,
        string python,
        string scriptPath,
        string standardInput)
    {
        string probeDirectory = Path.Combine(sandbox.RecordDirectory, "socket-audit");
        Directory.CreateDirectory(probeDirectory);
        File.WriteAllText(Path.Combine(probeDirectory, "sitecustomize.py"), SocketAuditProbeScript);
        if (File.Exists(sandbox.SocketAuditLogFile))
            File.Delete(sandbox.SocketAuditLogFile);

        ProcessStartInfo startInfo = VariantStartInfo(sandbox, python, scriptPath, false);
        // Child-only: ProcessRunner copies exactly this environment, so the host and
        // every other test keep their own PYTHONPATH.
        startInfo.Environment["PYTHONPATH"] = probeDirectory;
        startInfo.Environment["KYBER_SOCKET_AUDIT_LOG"] = sandbox.SocketAuditLogFile;
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        ProcessResult result = ProcessRunner.Run(startInfo, standardInput, TimeSpan.FromSeconds(30));
        AssertProbeInitialized(sandbox.SocketAuditLogFile, result);
        return result;
    }

    /// <summary>
    /// The probe writes a READY sentinel at import and reports recording failures on
    /// stderr instead of swallowing them. Either missing here means the observation is
    /// broken, so the run fails rather than passing on an empty log.
    /// </summary>
    private static void AssertProbeInitialized(string auditLogFile, ProcessResult result)
    {
        Assert.DoesNotContain(
            "kyber-socket-audit:",
            result.StandardError,
            StringComparison.Ordinal);
        string? first = File.Exists(auditLogFile)
            ? File.ReadAllLines(auditLogFile).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
            : null;
        Assert.True(
            string.Equals(first, SocketAuditReadyLine, StringComparison.Ordinal),
            "The socket-audit probe did not initialize (no READY sentinel); an empty log proves nothing, so this run fails.");
    }

    /// <summary>
    /// Bounded loopback connects to a private listener this task owns, each paired with
    /// its accept — never the artifact's process, never the user's real 4318. The
    /// returned count proves the connections really completed. No sleeps, no hangs: each
    /// accept pairs a connect that already returned.
    /// </summary>
    private static int EmitAndAcceptUnrelatedConnections()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int accepted = 0;
        for (int i = 0; i < 20; i++)
        {
            using TcpClient connector = new();
            connector.Connect(IPAddress.Loopback, port);
            using TcpClient server = listener.AcceptTcpClient();
            accepted++;
        }

        return accepted;
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

    private static ProcessResult RunCommandCodeHarness(
        Sandbox sandbox,
        string node,
        string artifactPath,
        string reportPath,
        string mode,
        string? extraEnvironment = null)
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
        startInfo.ArgumentList.Add(sandbox.CommandCodeHarnessPath);
        sandbox.ConfigureEnvironment(startInfo, false);
        startInfo.Environment["CC_ARTIFACT"] = artifactPath;
        startInfo.Environment["CC_FIXTURE"] = FixturePath(CommandCodeFixture);
        startInfo.Environment["CC_REPORT"] = reportPath;
        startInfo.Environment["CC_MODE"] = mode;
        if (extraEnvironment is not null)
            foreach (string pair in extraEnvironment.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=', 2);
                if (parts.Length == 2)
                    startInfo.Environment[parts[0]] = parts[1];
            }
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

    /// <summary>
    /// Any socket counts, not just the collector port: the contract is that the artifact
    /// opens no socket at all. The probe's READY sentinel is not a connection.
    /// </summary>
    private static void AssertNoSocketConnects(string auditLogFile, string subject)
    {
        string[] attempts = File.Exists(auditLogFile)
            ?
            [
                .. File.ReadAllLines(auditLogFile)
                    .Where(line => !string.IsNullOrWhiteSpace(line) &&
                                   !string.Equals(line, SocketAuditReadyLine, StringComparison.Ordinal))
            ]
            : [];
        Assert.True(
            attempts.Length == 0,
            $"{subject} opened a socket connection ({string.Join("; ", attempts)}); the OTLP post is replaced by the KyberDash hand-off (D10).");
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

    // Polls rather than watching. A FileSystemWatcher created after the existence check misses a
    // file that appears while the watcher is still starting (slow on macOS), so the wait then ran
    // out its whole timeout and passed on the final check: a 30 second stall in a passing test.
    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (clock.Elapsed >= timeout)
                return false;

            Thread.Sleep(20);
        }

        return true;
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
            SocketAuditLogFile = Path.Combine(RecordDirectory, "socket-audit.log");
            HarnessPath = Path.Combine(temp.Path, "pi-harness.mjs");

            File.WriteAllText(GitBranchFile, SyntheticBranch + "\n");
            File.WriteAllText(GitPorcelainFile, SyntheticPorcelainDirty);
            File.WriteAllText(HarnessPath, PiHarnessScript);
            CommandCodeHarnessPath = Path.Combine(temp.Path, CommandCodeHarnessFileName);
            File.WriteAllText(CommandCodeHarnessPath, CommandCodeHarnessScript);

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

        public string SocketAuditLogFile { get; }

        public string HarnessPath { get; }

        public string CommandCodeHarnessPath { get; }

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

    /// <summary>Sentinel the probe writes at import; its absence means the hook never loaded.</summary>
    private const string SocketAuditReadyLine = "READY";

    /// <summary>
    /// Child-scoped audit, loaded as <c>sitecustomize</c> through a child-only
    /// <c>PYTHONPATH</c>. The hook fires before the attempt, so refused and swallowed
    /// connects are still recorded; only endpoints are logged, never payloads. Import
    /// writes READY (a missing log or sentinel fails the run), and a recording failure
    /// is reported on stderr instead of swallowed. Stdlib only.
    /// </summary>
    private const string SocketAuditProbeScript = """
                                                  import os
                                                  import sys
                                                  _LOG = os.environ.get("KYBER_SOCKET_AUDIT_LOG", "")
                                                  if not _LOG:
                                                      raise RuntimeError("KYBER_SOCKET_AUDIT_LOG is not set; socket audit cannot record")
                                                  with open(_LOG, "w", encoding="utf-8") as handle:
                                                      handle.write("READY\n")
                                                  def _hook(event, args):
                                                      if event != "socket.connect":
                                                          return
                                                      try:
                                                          with open(_LOG, "a", encoding="utf-8") as handle:
                                                              handle.write(repr(args[1]) + "\n")
                                                      except OSError as exc:
                                                          os.write(2, ("kyber-socket-audit: RECORD-FAILED %r\n" % (exc,)).encode("utf-8", "replace"))
                                                  sys.addaudithook(_hook)
                                                  """;

    /// <summary>
    /// Negative-control fixture: one bounded connect attempt at the collector port,
    /// recorded refused or accepted alike, with nothing sent.
    /// </summary>
    private const string DeliberateCollectorConnectScript = """
                                                            import socket
                                                            sock = socket.socket()
                                                            sock.settimeout(5)
                                                            try:
                                                                sock.connect(("127.0.0.1", 4318))
                                                            except OSError:
                                                                pass
                                                            finally:
                                                                sock.close()
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

    /// <summary>
    /// The stub ModApi the CommandCode mod is driven through. It records
    /// <c>cmd.ui.setStatus</c> calls, captures the registered hooks, answers identity getters from
    /// the fixture, and fires <c>onSessionStart</c> then <c>onTurnEnd</c> with the fixture payload.
    /// </summary>
    private const string CommandCodeHarnessScript = """
                                                      import { after, test } from 'node:test'
                                                      import assert from 'node:assert/strict'
                                                      import { readFileSync, writeFileSync } from 'node:fs'
                                                      import { pathToFileURL } from 'node:url'

                                                      const report = { setStatus: [], error: null }

                                                      const fixture = JSON.parse(readFileSync(process.env.CC_FIXTURE, 'utf8'))
                                                      const tuiMode = process.env.CC_MODE === 'tui'
                                                      const noCaps = process.env.CC_NO_CAPS === '1'
                                                      const hooksThrow = process.env.CC_HOOKS_THROW === '1'
                                                      const poisonTurn = process.env.CC_POISON_TURN === '1'

                                                      const hooks = {}
                                                      const state = new Map()
                                                      const uiBase = {
                                                        setStatus: (text) => { report.setStatus.push(text === null ? '' : String(text)) },
                                                        notify: () => {},
                                                      }
                                                      const cmd = {
                                                        cwd: fixture.cwd,
                                                        getSessionName: () => undefined,
                                                        getModel: () => fixture.model,
                                                        getContextUsage: () => ({
                                                          tokens: fixture.usage?.totalTokens ?? null,
                                                          contextWindow: fixture.contextWindow ?? null,
                                                          percent: null,
                                                        }),
                                                        state: {
                                                          get: (key) => (state.has(key) ? state.get(key) : undefined),
                                                          set: (key, value) => { state.set(key, value) },
                                                          subscribe: () => ({ dispose() {} }),
                                                        },
                                                        exec: async () => ({ stdout: 'kyber/synthetic-branch\\n', code: 0 }),
                                                        ui: noCaps ? uiBase : { ...uiBase, capabilities: { status: tuiMode } },
                                                        hooks: (registered) => {
                                                          if (hooksThrow) throw new Error('synthetic hooks rejection')
                                                          Object.assign(hooks, registered)
                                                        },
                                                        on: () => ({ dispose() {} }),
                                                      }

                                                      after(() => {
                                                        writeFileSync(process.env.CC_REPORT, JSON.stringify(report))
                                                      })

                                                      test('the mod registers and renders against the stub ModApi', async () => {
                                                        const module = await import(pathToFileURL(process.env.CC_ARTIFACT).href)
                                                        const factory = module.default
                                                        assert.equal(typeof factory, 'function', 'the mod must default-export a factory function')
                                                        factory(cmd)
                                                        if (hooksThrow) return
                                                        assert.equal(typeof hooks.onSessionStart, 'function', 'the mod must register onSessionStart')
                                                        assert.equal(typeof hooks.onTurnEnd, 'function', 'the mod must register onTurnEnd')
                                                        await hooks.onSessionStart({ source: 'startup' })
                                                        const next = await hooks.onTurnEnd({
                                                          turnNumber: fixture.turnNumber,
                                                          hadToolCalls: true,
                                                          usage: fixture.usage,
                                                        })
                                                        if (next && typeof next === 'object') {
                                                          for (const [key, value] of Object.entries(next)) state.set(key, value)
                                                        }
                                                        if (poisonTurn) {
                                                          await hooks.onTurnEnd({
                                                            turnNumber: NaN,
                                                            hadToolCalls: true,
                                                            usage: { inputTokens: NaN, outputTokens: Infinity, cachedTokens: -5, totalTokens: 1e21 },
                                                          })
                                                        }
                                                      })
                                                      """;
}
