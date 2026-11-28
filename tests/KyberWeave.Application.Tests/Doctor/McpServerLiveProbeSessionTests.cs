using KyberWeave.Application.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>
/// The live MCP probe's decisions over a scripted server session: which checks it reports, what it tells the operator to
/// do, and that no reply the server can send makes it throw. No process starts and no path reaches the disk; the same
/// behaviour over a real stdio child is the integration project's end-to-end proof.
/// </summary>
public sealed class McpServerLiveProbeSessionTests
{
    private const string Secret = "kw-doctor-test-secret-7f3a";

    private const string Initialize = DoctorHarness.InitializeReply;

    private readonly InMemoryFileSystem _files = new();

    private readonly FakeMcpStdioSessionFactory _session = new();

    private string Root => Path.Combine(_files.Root, "repo");

    private static DoctorOptions Options(TimeSpan? budget = null, string executable = "kyber-weave-mcp") =>
        new(TimeSpan.FromSeconds(600), budget ?? TimeSpan.FromSeconds(20), "codegraph", executable);

    private McpServerLiveProbe Probe() =>
        new(_session, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: _files.Root));

    private Task<IReadOnlyList<DoctorCheck>> RunAsync(string? root = null, DoctorOptions? options = null) =>
        Probe().RunAsync(root ?? Root, options ?? Options());

    private static DoctorCheck Check(IReadOnlyList<DoctorCheck> checks, string id) =>
        Assert.Single(checks, check => check.Id == id);

    private static string Call(string text) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

    private static string Provenance(string root, string dirty = "no") =>
        $"provenance: root={root} rev=abc123 dirty={dirty} documents=7";

    private FakeMcpStdioSessionFactory Healthy(string? text = null) =>
        _session.Reply(Initialize).Reply(Call(text ?? Provenance(Root)));

    /// <summary>Control: a server that answers for this checkout passes every MCP check.</summary>
    [Fact]
    public async Task AMatchingRootPassesAllMcpChecks()
    {
        _ = Healthy();

        IReadOnlyList<DoctorCheck> checks = await RunAsync();

        Assert.All(checks, check => Assert.True(check.Passed, check.Reason));
        Assert.Equal(["mcp.start", "mcp.handshake", "mcp.docs_explore", "mcp.root"], checks.Select(check => check.Id));
    }

    /// <summary>The asynchronous probe makes the same decisions over the same scripted exchange.</summary>
    [Fact]
    public async Task TheAsyncProbePassesAllMcpChecksForAMatchingRoot()
    {
        _ = Healthy();

        IReadOnlyList<DoctorCheck> checks = await Probe().RunAsync(Root, Options());

        Assert.All(checks, check => Assert.True(check.Passed, check.Reason));
        Assert.Equal(["mcp.start", "mcp.handshake", "mcp.docs_explore", "mcp.root"], checks.Select(check => check.Id));
    }

    /// <summary>The asynchronous probe fails a timed-out handshake and still disposes the session.</summary>
    [Fact]
    public async Task TheAsyncProbeFailsAHandshakeTimeoutAndKillsTheChild()
    {
        _ = _session.ReadFails(new TimeoutException());

        IReadOnlyList<DoctorCheck> checks = await Probe().RunAsync(Root, Options(TimeSpan.FromSeconds(1)));

        DoctorCheck handshake = Check(checks, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.Contains("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
        Assert.True(_session.Disposed);
    }

    /// <summary>The probe asks for the renamed docs tool, never the retired name, and keeps the stable check id.</summary>
    [Fact]
    public async Task TheProbeRequestsTheRenamedDocsToolAndKeepsItsCheckId()
    {
        _ = Healthy();

        IReadOnlyList<DoctorCheck> checks = await RunAsync();

        Assert.True(Check(checks, "mcp.docs_explore").Passed);
        string request = Assert.Single(_session.Sent, line => line.Contains("tools/call", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"" + DoctorHarness.ProbedTool + "\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"docs_explore\"", request, StringComparison.Ordinal);
    }

    /// <summary>The server starts for the repository the doctor resolved, with its CodeGraph refresh disabled.</summary>
    [Fact]
    public async Task TheServerIsStartedWithTheResolvedRootAndWithCodeGraphRefreshDisabled()
    {
        _ = Healthy();

        _ = await RunAsync();

        Assert.Equal("kyber-weave-mcp", _session.Executable);
        Assert.Contains("--repo-root", _session.Arguments);
        Assert.Contains(Root, _session.Arguments);
        Assert.Contains("--no-codegraph-refresh", _session.Arguments);
    }

    /// <summary>A server that never answers fails the handshake, names the budget in the remedy, and the session is disposed (the child killed).</summary>
    [Fact]
    public async Task AHandshakeTimeoutFailsMcpHandshakeAndKillsTheChild()
    {
        _ = _session.ReadFails(new TimeoutException());

        IReadOnlyList<DoctorCheck> checks = await RunAsync(options: Options(TimeSpan.FromSeconds(1)));

        DoctorCheck handshake = Check(checks, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Fix));
        Assert.Contains("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
        Assert.True(_session.Disposed);
    }

    /// <summary>A folder with no host configuration is told to run init, not to raise the timeout.</summary>
    [Fact]
    public async Task NoHostConfigurationNamesTheMissingFileAndTheCommandThatAddsIt()
    {
        _session.ErrorLine = "KW-MCP-ROOT-001: No Kyber-Weave host configuration was found for MCP root";
        _ = _session.ReadFails(new EndOfStreamException());

        DoctorCheck handshake = Check(await RunAsync(), "mcp.handshake");

        Assert.False(handshake.Passed);
        Assert.Contains(".kyber-weave/kyber-weave.yml", handshake.Reason, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs init", handshake.Fix, StringComparison.Ordinal);
        Assert.Contains(Root, handshake.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
    }

    /// <summary>A server that exits before answering is told to read its own error, not to raise the timeout.</summary>
    [Fact]
    public async Task AServerThatExitsBeforeAnsweringIsNotToldToRaiseTheTimeout()
    {
        _session.ErrorLine = "something else broke";
        _ = _session.ReadFails(new EndOfStreamException());

        DoctorCheck handshake = Check(await RunAsync(), "mcp.handshake");

        Assert.False(handshake.Passed);
        Assert.Contains("something else broke", handshake.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-mcp", handshake.Fix, StringComparison.Ordinal);
    }

    /// <summary>A docs tool that returns an error result fails mcp.docs_explore even though the process is alive.</summary>
    [Fact]
    public async Task ADocsToolErrorFailsMcpDocsExplore()
    {
        _ = _session.Reply(Initialize).Reply(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"index not loaded\"}]}}");

        IReadOnlyList<DoctorCheck> checks = await RunAsync();

        Assert.False(Check(checks, "mcp.docs_explore").Passed);
        Assert.DoesNotContain(checks, check => check.Id == "mcp.root");
    }

    /// <summary>A server answering from another checkout fails mcp.root even though the tool call succeeded.</summary>
    [Fact]
    public async Task AServerAnsweringFromAnotherCheckoutFailsMcpRoot()
    {
        _ = Healthy("provenance: root=/elsewhere/checkout");

        IReadOnlyList<DoctorCheck> checks = await RunAsync();

        Assert.True(Check(checks, "mcp.docs_explore").Passed);
        Assert.False(Check(checks, "mcp.root").Passed);
    }

    /// <summary>The provenance line carries metadata after the root, and a root with spaces is compared whole.</summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task ProvenanceRootWithMetadataPassesWhenTheRootMatches(string dirty)
    {
        string root = Path.Combine(_files.Root, "repo with spaces");
        _ = Healthy(Provenance(root, dirty));

        Assert.True(Check(await RunAsync(root), "mcp.root").Passed);
    }

    /// <summary>The same format naming a different repository still fails the root check.</summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task ProvenanceRootWithMetadataFailsWhenTheRootDiffers(string dirty)
    {
        string root = Path.Combine(_files.Root, "repo with spaces");
        _ = Healthy(Provenance(Path.Combine(_files.Root, "other repo with spaces"), dirty));

        Assert.False(Check(await RunAsync(root), "mcp.root").Passed);
    }

    /// <summary>An older server that emits only the bare root is still accepted.</summary>
    [Fact]
    public async Task ABareProvenanceRootIsStillAccepted()
    {
        string root = Path.Combine(_files.Root, "bare repo");
        _ = Healthy("provenance: root=" + root);

        Assert.True(Check(await RunAsync(root), "mcp.root").Passed);
    }

    /// <summary>The branch and repo fields the header gained do not turn a correct server into a wrong-repository diagnosis.</summary>
    [Theory]
    [InlineData("repo")]
    [InlineData("repo with spaces")]
    [InlineData("my rev=evil")]
    public async Task ProvenanceWithBranchAndRepoMetadataStillPassesMcpRoot(string folder)
    {
        string root = Path.Combine(_files.Root, folder);
        _ = Healthy(
            "provenance: root=" + root + " branch=main repo=" + Path.Combine(root, ".git")
            + " rev=abc123 dirty=no documents=7");

        DoctorCheck check = Check(await RunAsync(root), "mcp.root");

        Assert.True(check.Passed, check.Reason);
    }

    /// <summary>A server that closes its input after initialize fails a check instead of throwing out of the probe.</summary>
    [Fact]
    public async Task AServerThatClosesItsInputReportsAFailureInsteadOfThrowing()
    {
        _session.SendFailure = (2, new IOException("Broken pipe"));
        _ = _session.Reply(Initialize);

        IReadOnlyList<DoctorCheck> checks = await RunAsync();

        Assert.Contains(checks, check => !check.Passed);
        Assert.False(Check(checks, "mcp.docs_explore").Passed);
    }

    /// <summary>A reply whose members are not the expected shapes fails a check instead of throwing.</summary>
    [Theory]
    [InlineData("initialize-is-an-array")]
    [InlineData("result-is-an-array")]
    [InlineData("content-item-is-a-number")]
    public async Task ANonObjectReplyFailsACheckInsteadOfThrowing(string shape)
    {
        string initialize = shape == "initialize-is-an-array" ? "[1,2]" : Initialize;
        string call = shape switch
        {
            "result-is-an-array" => """{"jsonrpc":"2.0","id":2,"result":[42]}""",
            "content-item-is-a-number" => """{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[42]}}""",
            _ => Call("provenance: root=/elsewhere"),
        };
        _ = _session.Reply(initialize).Reply(call);

        Assert.Contains(await RunAsync(), check => !check.Passed);
    }

    /// <summary>A provenance root the platform cannot canonicalise fails mcp.root instead of throwing.</summary>
    [Fact]
    public async Task AProvenanceRootWithANulCharacterFailsMcpRootInsteadOfThrowing()
    {
        _ = Healthy("provenance: root=/tmp/ev\0il");

        Assert.False(Check(await RunAsync(), "mcp.root").Passed);
    }

    /// <summary>A server that cannot start fails mcp.start, and no reason or fix echoes anything the process holds.</summary>
    [Fact]
    public async Task AStartFailureFailsMcpStartWithoutDisclosingTheEnvironment()
    {
        _session.StartFailure = new System.ComponentModel.Win32Exception(2, "No such file or directory " + Secret);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(options: Options(executable: "kyber-weave-mcp-absent-for-test"));

        DoctorCheck start = Assert.Single(checks);
        Assert.Equal("mcp.start", start.Id);
        Assert.False(start.Passed);
        Assert.DoesNotContain(Secret, start.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, start.Fix ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>A reply over the session's line cap fails mcp.handshake instead of throwing.</summary>
    [Fact]
    public async Task AReplyOverTheLineCapFailsMcpHandshakeInsteadOfThrowing()
    {
        _ = _session.ReadFails(new InvalidDataException("line cap"));

        Assert.False(Check(await RunAsync(), "mcp.handshake").Passed);
    }

    /// <summary>A budget too large to double inside an int still prints a positive timeout in the remedy.</summary>
    [Fact]
    public async Task AHugeBudgetPrintsAPositiveTimeoutInTheRemedy()
    {
        _ = _session.Reply(Initialize).ReadFails(new TimeoutException());

        DoctorCheck docsExplore = Check(await RunAsync(options: Options(TimeSpan.FromSeconds(2_000_000_000))), "mcp.docs_explore");

        Assert.False(docsExplore.Passed);
        string fix = docsExplore.Fix ?? string.Empty;
        int marker = fix.IndexOf("--mcp-timeout ", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"The remedy '{fix}' does not name a timeout to raise.");
        string suggested = fix[(marker + "--mcp-timeout ".Length)..].Trim();
        Assert.True(
            int.TryParse(suggested, out int seconds) && seconds > 0,
            $"The remedy suggested '{suggested}', which is not a timeout --mcp-timeout accepts.");
    }
}
