namespace KyberDash.Application.Tests;

/// <summary>A scratch directory that is deleted when the test finishes.</summary>
public class TempDirectory : IDisposable
{
    private bool disposed;

    /// <summary>Creates a new, empty directory under the system temporary root.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kw-app-" + Guid.NewGuid().ToString("N"));
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
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the directory when disposing, tolerating files still held open briefly.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || disposed)
        {
            return;
        }

        disposed = true;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
