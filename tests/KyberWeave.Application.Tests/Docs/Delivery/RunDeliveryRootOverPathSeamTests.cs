using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Delivery;

/// <summary>The delivery resolves the run's worktree through the path seam and runs git there.</summary>
public sealed class RunDeliveryRootOverPathSeamTests
{
    [Theory]
    [InlineData("/kw/work", "/kw/work/repo")]
    [InlineData("/kw/other", "/kw/other/repo")]
    public void ARelativeWorktreeIsResolvedAgainstTheInjectedWorkingDirectoryAndGitRunsThere(string workingDirectory, string expectedRoot)
    {
        InMemoryFileSystem disk = new();
        ScriptedProcessExecutor git = new(_ => new ProcessResult(0, string.Empty, string.Empty));
        RunDeliveryPorts ports = new(
            new InMemoryRepositoryFileStore(disk),
            git,
            null!,
            null!,
            new FakeProcessEnvironment(currentDirectory: workingDirectory),
            TimeProvider.System,
            new FakePathResolver(disk),
            new FakeEmbeddedResources());

        DeliveryOutcome outcome = new RunDelivery("repo", KyberWeaveConfigDefaults.Product, git, "/kw/home", ports)
            .Run(new DeliveryRequest("Lists now export."));

        Assert.False(outcome.Delivered);
        Assert.Equal(expectedRoot, Assert.Single(git.Calls).WorkingDirectory);
    }
}
