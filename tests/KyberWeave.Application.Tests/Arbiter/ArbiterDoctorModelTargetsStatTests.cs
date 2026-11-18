using KyberWeave.Application.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the review fix to <c>arbiter doctor</c>'s model targets check: reading a file's write
/// time must never throw out of the doctor. A missing file reads as missing, and a file whose stat is
/// denied reads as unreadable, so the check degrades to its <c>KW-ARB-MODEL-001</c> warning.
/// </summary>
public sealed class ArbiterDoctorModelTargetsStatTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void WriteTime_OfAPresentFile_IsReadAsPresent()
    {
        string path = _host.Write(_host.InWork("model-targets.claude.json"), "{}");

        bool present = _host.Command<ArbiterDoctorService>().TryGetWriteTimeUtc(path, out DateTime written);

        Assert.True(present);
        Assert.True(written > DateTime.UnixEpoch);
    }

    [Fact]
    public void WriteTime_OfAMissingFile_IsReadAsMissingNotAsOldestDate()
    {
        string path = _host.InWork("absent.json");

        bool present = _host.Command<ArbiterDoctorService>().TryGetWriteTimeUtc(path, out _);

        Assert.False(present);
    }

    [Fact]
    public void WriteTime_WhenTheStatIsDenied_ReturnsFalseInsteadOfThrowing()
    {
        string path = _host.Write(_host.InWork("locked", "model-targets.claude.json"), "{}");
        _host.Files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.GetLastWriteTimeUtc),
            new UnauthorizedAccessException("The stat is denied."));

        bool present = _host.Command<ArbiterDoctorService>().TryGetWriteTimeUtc(path, out _);

        Assert.False(present);
    }
}
