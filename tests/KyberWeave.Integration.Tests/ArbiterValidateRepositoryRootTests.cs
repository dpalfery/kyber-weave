using System.Runtime.CompilerServices;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Tests.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The shipped repository's own configuration passes <c>arbiter validate</c>, read from the checkout on disk.</summary>
public sealed class ArbiterValidateRepositoryRootTests
{
    [Fact]
    public void Validate_RepositoryRoot_ExitsZero()
    {
        ArbiterValidateCommand command = ArbiterTestServices.Command<ArbiterValidateCommand>();
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterSettings { Path = RepositoryRoot() }));

        Assert.Equal(0, execution.Result);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}
