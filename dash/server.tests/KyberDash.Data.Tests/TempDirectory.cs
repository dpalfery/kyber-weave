using Xunit;
namespace KyberDash.Data.Tests;

/// <summary>A scratch directory that is deleted when the test finishes.</summary>
public sealed class TempDirectory : IDisposable
{
    private bool disposed;

    /// <summary>Creates a new, empty directory under the system temporary root.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kw-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the scratch directory.</summary>
    public string Path { get; }

    /// <summary>Combines <paramref name="relativePath"/> onto <see cref="Path"/>.</summary>
    /// <param name="relativePath">Path relative to the scratch directory.</param>
    /// <returns>The combined absolute path.</returns>
    public string Combine(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return System.IO.Path.Combine(Path, relativePath);
    }

    /// <summary>Deletes the directory and everything under it, tolerating files still held open briefly.</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        GC.SuppressFinalize(this);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
