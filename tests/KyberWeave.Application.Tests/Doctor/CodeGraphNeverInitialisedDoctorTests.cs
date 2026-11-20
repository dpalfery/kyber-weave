using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>
/// Pins what the doctor decides when <c>.codegraph/</c> exists but was never initialised, over the in-memory
/// doctor world: the folder is set up with <c>codegraph init</c> rather than failing the run, the fix printed
/// is the command that works, and a message that merely contains "not initialized" is not treated as one.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory twins of the same four cases in
/// <c>tests/KyberWeave.Integration.Tests/McpLiveProbeTests.cs</c>. Those keep the production container's
/// wiring over the real disk and a shell-stub MCP server; these prove the decision itself with no disk and no
/// child process.
/// </para>
/// <para>
/// D23: <c>doctor --check</c> promises it "never runs init or index", so running <c>init</c> over a folder whose
/// index is merely absent must be the one deliberate exception the provisioning and ensure modes make.
/// </para>
/// </remarks>
public sealed class CodeGraphNeverInitialisedDoctorTests
{
    private const string McpExecutable = "kyber-weave-mcp";

    private readonly InMemoryDoctorWorld _world = new();

    private string Root => _world.Root;

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    /// <summary>
    /// A <c>.codegraph/</c> folder that was never initialised, such as one holding only a committed
    /// <c>.gitignore</c>, is set up with <c>codegraph init</c> rather than failing the run.
    /// </summary>
    [Theory]
    [InlineData(DoctorMode.Ensure)]
    [InlineData(DoctorMode.Provision)]
    public void FolderNeverInitialised_IsSetUpWithInit(DoctorMode mode)
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal)
                ? new ProcessResult(1, string.Empty, "✗ CodeGraph not initialized in " + Root)
                : DoctorHarness.Ok("1.5.0"));

        DoctorReport report = Run(mode, executor);

        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("index", StringComparer.Ordinal));
    }

    /// <summary>
    /// A <c>.codegraph/</c> folder that was never initialised makes <c>sync</c> and <c>index</c> both refuse. The fix the
    /// doctor prints must be the one that works, <c>codegraph init .</c>, not the command that just failed.
    /// </summary>
    [Fact]
    public void Ensure_FolderNeverInitialised_PrintsInitAsTheFix()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments =>
            {
                if (arguments.Contains("--version", StringComparer.Ordinal))
                    return DoctorHarness.Ok("1.5.0");
                return arguments.Contains("init", StringComparer.Ordinal)
                    ? new ProcessResult(1, string.Empty, "disk full")
                    : new ProcessResult(1, string.Empty, "✗ CodeGraph not initialized in " + Root);
            });

        DoctorReport report = Run(DoctorMode.Ensure, executor);

        DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
        Assert.False(index.Passed);
        Assert.Contains("not set up", index.Reason, StringComparison.Ordinal);
        Assert.Contains("CodeGraph not initialized", index.Reason, StringComparison.Ordinal);
        Assert.Equal("codegraph init .", index.Fix);
    }

    /// <summary>
    /// Another message that merely contains "not initialized" is an ordinary failure. Running <c>init</c> for it would
    /// hide the real error behind advice about a folder that is fine.
    /// </summary>
    [Fact]
    public void Provision_UnrelatedNotInitializedMessage_DoesNotRunInit()
    {
        _world.Files.CreateDirectory(IndexDirectory);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal)
                ? new ProcessResult(1, string.Empty, "telemetry not initialized, sync failed")
                : DoctorHarness.Ok("1.5.0"));

        DoctorReport report = Run(DoctorMode.Provision, executor);

        Assert.Equal("codegraph sync .", DoctorHarness.CheckNamed(report, "codegraph.index").Fix);
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
    }

    private DoctorReport Run(DoctorMode mode, ScriptedProcessExecutor executor)
    {
        _world.HealthyMcp(Root);
        return _world.Doctor.Run(
            Root,
            mode,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null,
            _world.Locks);
    }
}
