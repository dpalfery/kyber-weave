using System.Text;
using KyberDash.Data.Collector.Sources;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>Real-file tests for the bounded read of <see cref="PhysicalHarnessFileSystem"/>.</summary>
public sealed class PhysicalHarnessFileSystemTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("kyberdash-bounded-read-").FullName;

    [Fact]
    public void ReadAllText_FileWithinTheLimit_ReturnsItsTextWithoutTheByteOrderMark()
    {
        string path = Path.Combine(directory, "c.json");
        File.WriteAllText(path, "{\"c\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string text = PhysicalHarnessFileSystem.Instance.ReadAllText(path, new FileInfo(path).Length);

        Assert.Equal("{\"c\":1}", text);
    }

    [Fact]
    public void ReadAllText_FileLargerThanTheLimit_Throws()
    {
        string path = Path.Combine(directory, "c.json");
        File.WriteAllText(path, new string('x', 17));

        Assert.Throws<InvalidDataException>(() => PhysicalHarnessFileSystem.Instance.ReadAllText(path, 16));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the scratch directory when disposing.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
