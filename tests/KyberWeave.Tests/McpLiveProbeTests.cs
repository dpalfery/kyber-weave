using Xunit;
using KyberWeave.Core.Doctor;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the live MCP proof (D1, D7, D8, D9) through <see cref="RepositoryDoctor.Run"/>. CodeGraph is answered by
/// the scripted executor and the server is a trivial shell stub, so the proof runs over a real stdio session with
/// no real <c>kyber-weave-mcp</c>. The stubs live in a temp directory and are removed with it.
/// </summary>
/// <remarks>
/// Test names use <c>McpHandshake</c> and <c>McpRoot</c> where the plan writes <c>mcp.handshake</c> and
/// <c>mcp.root</c>: a dot is not legal in a C# method name. The check ids asserted are the dotted ones.
/// </remarks>
public sealed class McpLiveProbeTests : IDisposable
{
    private const string Secret = "kw-doctor-test-secret-7f3a";

    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();

    public void Dispose()
    {
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

    /// <summary>
    /// Control: a server that answers for this checkout passes every MCP check. Without this the failure tests below
    /// could pass against a stub that never works.
    /// </summary>
    [Fact]
    public void McpLiveProbe_MatchingRoot_PassesAllMcpChecks()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "mcp.start").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.handshake").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.docs_explore").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The probe starts the server for the repository it resolved, by passing <c>--repo-root</c>. Without the flag the
    /// child inherits the doctor's working directory, so a healthy target can fail <c>mcp.root</c> or pass against the
    /// wrong checkout — the exact confusion the probe exists to prevent.
    /// </summary>
    [Fact]
    public void McpLiveProbe_StartsTheServerWithTheResolvedRoot()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string argvPath = Path.Combine(_stubs.Path, "argv.txt");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.RecordingStub(_stubs.Path, Root, argvPath)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(0, report.ExitCode);
        string[] argv = File.ReadAllLines(argvPath);
        Assert.Contains("--repo-root", argv);
        Assert.Contains(Root, argv);
    }

    /// <summary>
    /// A server that starts but never answers the handshake fails <c>mcp.handshake</c> with exit 1, and the budget ends
    /// the wait. The child is killed on that path; the stub's <c>sleep</c> would otherwise outlive the run.
    /// </summary>
    /// <remarks>
    /// D7: the session has a total budget and kills the tree in <c>finally</c>. Observing the kill from outside the
    /// session needs a process seam the contract does not expose, so the assertion here is the outcome.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_HandshakeTimeout_KillsChild_Exits1_McpHandshake()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.SilentStub(_stubs.Path), TimeSpan.FromSeconds(1)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        DoctorCheck handshake = DoctorHarness.CheckNamed(report, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Fix));
    }

    /// <summary>
    /// A server whose <c>docs_explore</c> returns an error result fails <c>mcp.docs_explore</c> with exit 1. Process
    /// liveness is not enough: <c>docs_status</c> alone would pass here, which is why D8 chose <c>docs_explore</c>.
    /// </summary>
    [Fact]
    public void McpLiveProbe_DocsExploreError_Exits1()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ErroringDocsExploreStub(_stubs.Path, Root)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.docs_explore").Passed);
    }

    /// <summary>
    /// A server answering from another checkout fails <c>mcp.root</c> with exit 1, even though <c>docs_explore</c>
    /// succeeded. The root check is what catches a server pointed at the wrong repository.
    /// </summary>
    /// <remarks>D8: the <c>provenance: root=</c> value must equal <c>git rev-parse --show-toplevel</c>.</remarks>
    [Fact]
    public void McpLiveProbe_RootMismatch_Exits1_McpRoot()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.WrongRootStub(_stubs.Path)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// A server that cannot start fails <c>mcp.start</c>, and no check reason or fix line echoes the environment. The
    /// secret is injected through the environment delegate, so the test never touches the process environment.
    /// </summary>
    /// <remarks>D9: a failure never echoes the child's environment or stderr beyond the first error line.</remarks>
    [Fact]
    public void McpLiveProbe_StartFailure_DoesNotDiscloseEnvironment()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options("kyber-weave-mcp-absent-for-test"),
            DoctorHarness.Executor(Root),
            name => name == "KW_DOCTOR_TEST_SECRET" ? Secret : null);

        Assert.False(DoctorHarness.CheckNamed(report, "mcp.start").Passed);
        Assert.Equal(1, report.ExitCode);
        foreach (DoctorCheck check in report.Checks)
        {
            Assert.DoesNotContain(Secret, check.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, check.Fix ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
