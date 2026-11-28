using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The doctor's live MCP probe starts a real server process and reads the root it reports. Regression test for Kilo
/// review comment 11 on PR #527: a root whose path holds the probe's own revision marker still matches.
/// </summary>
public sealed class DoctorMcpProbePathTests
{
    [Fact]
    public async Task Item11_McpLiveProbe_PathContainingRevMarker_MatchesCorrectRoot()
    {
        using TempDirectory root = new();
        using TempDirectory stubs = new();

        string dirWithRev = Path.Combine(root.Path, "my rev= test");
        Directory.CreateDirectory(dirWithRev);
        Directory.CreateDirectory(Path.Combine(dirWithRev, ".codegraph"));

        string stub = DoctorHarness.WriteStub(
            stubs.Path,
            "read _initialize\n" +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"serverInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}'\n" +
            "read _notif\n" +
            "read _tool\n" +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"provenance: root=" + dirWithRev + " rev=abc1234 dirty=false documents=7\"}]}}'\n");

        DoctorReport report = await DoctorTestServices.Doctor.RunAsync(
            dirWithRev,
            DoctorMode.Check,
            DoctorHarness.Options(stub),
            DoctorHarness.Executor(dirWithRev),
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }
}
