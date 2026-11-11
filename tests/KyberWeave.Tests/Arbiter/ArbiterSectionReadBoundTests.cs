using KyberWeave.Core.Arbiter.Sections;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 4538775151: the section selector's size was checked with <c>FileInfo.Length</c> and then the file was
/// read with no bound, so a file swapped for a larger one between the check and the read was read whole. The read is
/// now bounded itself, and a file over the cap is refused, not read.
/// </summary>
public sealed class ArbiterSectionReadBoundTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task BoundedRead_FileOneByteOverTheLimit_IsRefusedNotRead()
    {
        const long limit = 64;
        string path = Path.Combine(_temp.Path, "over.md");
        await File.WriteAllTextAsync(path, new string('a', (int)limit + 1));

        string? text = await SectionSelector.ReadBoundedAsync(path, limit, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public async Task BoundedRead_FileExactlyAtTheLimit_IsRead()
    {
        const long limit = 64;
        string path = Path.Combine(_temp.Path, "at.md");
        await File.WriteAllTextAsync(path, new string('a', (int)limit));

        string? text = await SectionSelector.ReadBoundedAsync(path, limit, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Equal((int)limit, text.Length);
    }
}
