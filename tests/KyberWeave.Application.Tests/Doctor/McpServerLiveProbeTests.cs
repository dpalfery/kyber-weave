using KyberWeave.Application.Doctor;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>The probe's root check, over a scripted server and the path seam, so no process starts and no path reaches the disk.</summary>
public sealed class McpServerLiveProbeTests
{
    private static readonly DoctorOptions Options = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "codegraph", "kw-mcp");

    private static McpServerLiveProbe Probe(string servedRoot) =>
        new(
            new ScriptedFactory(servedRoot),
            new FakePathResolver(new InMemoryFileSystem()),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"));

    private static DoctorCheck RootCheck(IReadOnlyList<DoctorCheck> checks) => Assert.Single(checks, c => c.Id == "mcp.root");

    [Theory]
    [InlineData("/kw/repo")]
    [InlineData("/kw/repo/")]
    [InlineData("/kw/other/../repo")]
    public void AServedRootThatNamesTheSameDirectoryPasses(string served)
    {
        DoctorCheck check = RootCheck(Probe(served).Run("/kw/repo", Options));

        Assert.True(check.Passed);
    }

    [Fact]
    public void ARelativeServedRootIsReadAgainstTheWorkingDirectory()
    {
        DoctorCheck check = RootCheck(Probe("../work/../work/repo").Run("/kw/work/repo", Options));

        Assert.True(check.Passed);
    }

    [Fact]
    public void AServedRootThatNamesAnotherDirectoryFails()
    {
        DoctorCheck check = RootCheck(Probe("/kw/elsewhere").Run("/kw/repo", Options));

        Assert.False(check.Passed);
    }

    [Fact]
    public void AServedRootThePlatformCannotResolveFailsInsteadOfThrowing()
    {
        DoctorCheck check = RootCheck(Probe("/kw/re\0po").Run("/kw/repo", Options));

        Assert.False(check.Passed);
    }

    private sealed class ScriptedFactory(string servedRoot) : IMcpStdioSessionFactory
    {
        public IMcpStdioSession Start(string executable, IReadOnlyList<string> arguments, TimeSpan budget) =>
            new ScriptedSession(servedRoot);
    }

    private sealed class ScriptedSession(string servedRoot) : IMcpStdioSession
    {
        private readonly Queue<string> _replies = new(
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"provenance: root="
                + servedRoot.Replace("\0", "\\u0000", StringComparison.Ordinal)
                + " rev=abc dirty=false documents=3\"}]}}"
        ]);

        public string? FirstErrorLine => null;

        public string? WaitForFirstErrorLine(TimeSpan wait) => null;

        public void SendLine(string line)
        {
        }

        public string ReadLine() => _replies.Dequeue();

        public void Dispose()
        {
        }
    }
}
