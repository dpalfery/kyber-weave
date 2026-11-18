using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The containment check on <c>arbiter comment</c> uses the same root as the read. With <c>--root</c> set to a
/// subdirectory, a cited file that sits inside the git repository but outside that root is refused. The check used to
/// test the git root, the read then used <c>--root</c>, and the read silently found no excerpt.
/// </summary>
public sealed class ArbiterCommentRootContainmentCliTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void ACitedFileOutsideTheRootIsRefusedEvenInsideTheGitRepository()
    {
        // A git repository whose --root is a subdirectory. The cited file is an absolute path inside the repository,
        // outside that root, so the check against the git root passes it while the read under --root cannot.
        _host.Files.CreateDirectory(_host.InWork(".git"));
        string subRoot = _host.InWork("sub");
        _host.Files.CreateDirectory(subRoot);
        string cited = _host.Write(_host.InWork("secret.md"), "# Secret\nText.\n");

        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();
        ArbiterCommandRun execution = _host.Run(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = subRoot,
                Path = cited,
                Line = 1,
                Body = "Shorten this heading.",
            }));

        Assert.Equal(1, execution.ExitCode);
        ConsoleOutputAssert.ContainsUnwrapped("was not found under the repository root.", execution.Output);
        Assert.DoesNotContain("decision", execution.Output, StringComparison.OrdinalIgnoreCase);
    }
}
