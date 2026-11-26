using KyberWeave.Core;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// The gate that limits a prior-results file to the run's scratch folder, over a checkout held in memory: it asks
/// the same file system the read goes through, so links are judged where they are read.
/// </summary>
public sealed class TaskPromptScratchGateTests
{
    private const string Root = "/repo";

    private readonly InMemoryFileSystem _disk = new();

    public TaskPromptScratchGateTests()
    {
        _disk.CreateDirectory(Root + "/" + ScratchLayout.Root);
        _disk.CreateDirectory(Root + "/src");
    }

    [Fact]
    public void AFileInsideTheScratchFolderPasses()
    {
        _disk.WriteAllText(Root + "/" + ScratchLayout.Root + "/prior.md", "results");

        Assert.True(TaskPromptService.IsScratchFile(_disk, Root, ScratchLayout.Root + "/prior.md"));
    }

    [Fact]
    public void AFileOutsideTheScratchFolderIsRefused()
    {
        _disk.WriteAllText(Root + "/src/secret.md", "secret");

        Assert.False(TaskPromptService.IsScratchFile(_disk, Root, "src/secret.md"));
    }

    [Fact]
    public void AFileLinkInsideScratchPointingOutsideIsRefused()
    {
        _disk.WriteAllText(Root + "/src/secret.md", "secret");
        _disk.CreateFileLink(Root + "/" + ScratchLayout.Root + "/prior.md", Root + "/src/secret.md");

        Assert.False(TaskPromptService.IsScratchFile(_disk, Root, ScratchLayout.Root + "/prior.md"));
    }

    [Fact]
    public void ADirectoryLinkInsideScratchPointingOutsideIsRefused()
    {
        _disk.WriteAllText(Root + "/src/secret.md", "secret");
        _disk.CreateDirectoryLink(Root + "/" + ScratchLayout.Root + "/out", Root + "/src");

        Assert.False(TaskPromptService.IsScratchFile(_disk, Root, ScratchLayout.Root + "/out/secret.md"));
    }
}
