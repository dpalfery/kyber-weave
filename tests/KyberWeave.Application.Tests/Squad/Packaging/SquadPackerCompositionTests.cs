using KyberWeave.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Packaging;

/// <summary>
/// The packer lives in Application now and takes the file-system seam, so the application composition root registers
/// it: this fails if the registration is dropped or the seam is not available to it.
/// </summary>
public sealed class SquadPackerCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheArchiveWriterOverTheFileSystemSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        ISquadArchiveWriter writer = provider.GetRequiredService<ISquadArchiveWriter>();

        Assert.IsType<KyberWeave.Core.Squad.Packaging.SquadPacker>(writer);
        Assert.Throws<ArgumentException>(() => writer.PackApm(" ", "/nowhere", "1.0.0"));
    }
}
