using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Arbiter;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the append-only log contract against the real adapter, which appends under an exclusive lock file.</summary>
public sealed class ArbiterLogFilesContractTests : ArbiterLogStoreContractTests
{
    private readonly List<string> _directories = [];

    /// <inheritdoc />
    protected override IArbiterLogStore Store { get; } = new ArbiterLogFiles();

    /// <inheritdoc />
    protected override string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kyber-arbiter-log-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        return directory;
    }

    /// <inheritdoc />
    protected override string UnreadablePath(string directory) => Path.Combine(directory, "bad\0name.jsonl");

    /// <inheritdoc />
    protected override void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (string directory in _directories)
            {
                DeleteDirectory(directory);
            }
        }

        base.Dispose(disposing);
    }
}
