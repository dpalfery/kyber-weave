namespace KyberWeave.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kw-export-" + Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
