using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The containment check on <c>arbiter comment</c> uses the same root as the read. With <c>--root</c> set to a
/// subdirectory, a cited file that sits inside the git repository but outside that root is refused. The check used to
/// test the git root, the read then used <c>--root</c>, and the read silently found no excerpt.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterCommentRootContainmentCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ACitedFileOutsideTheRootIsRefusedEvenInsideTheGitRepository()
    {
        // A git repository whose --root is a subdirectory. The cited file is an absolute path inside the repository,
        // outside that root, so the check against the git root passes it while the read under --root cannot.
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".git"));
        string subRoot = Path.Combine(_temp.Path, "sub");
        Directory.CreateDirectory(subRoot);
        string cited = Path.Combine(_temp.Path, "secret.md");
        File.WriteAllText(cited, "# Secret\nText.\n");

        ArbiterCommentCommand command = new();
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = subRoot,
                Path = cited,
                Line = 1,
                Body = "Shorten this heading.",
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("was not found under the repository root.", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("decision", execution.Output, StringComparison.OrdinalIgnoreCase);
    }
}
