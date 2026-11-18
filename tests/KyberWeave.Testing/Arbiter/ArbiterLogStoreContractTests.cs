using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The contract every <see cref="IArbiterLogStore"/> must satisfy. Run it once against the in-memory fake and once
/// against the adapter that owns the exclusive append lock, so a unit test may trust what the fake reports.
/// </summary>
/// <remarks>
/// A derived class supplies the seam, a directory nothing else uses, a path the seam cannot open, and a way to remove
/// the directory again. The fake answers the unreadable path from a set; the adapter gets a path the platform refuses.
/// </remarks>
public abstract class ArbiterLogStoreContractTests : IDisposable
{
    /// <summary>The name every line of the suite is appended under.</summary>
    protected const string FileName = "log.jsonl";

    /// <summary>The log seam under test.</summary>
    protected abstract IArbiterLogStore Store { get; }

    /// <summary>A directory no other test touches, which need not exist yet.</summary>
    /// <returns>The directory.</returns>
    protected abstract string NewDirectory();

    /// <summary>A path under <paramref name="directory"/> that <see cref="Store"/> answers as unreadable.</summary>
    /// <param name="directory">The directory to build the path under.</param>
    /// <returns>The path.</returns>
    protected abstract string UnreadablePath(string directory);

    /// <summary>Removes what the suite created under <paramref name="directory"/>.</summary>
    /// <param name="directory">The directory.</param>
    protected abstract void DeleteDirectory(string directory);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Removes the directories the suite created.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    [Fact]
    public async Task EveryAppendedLineReadsBackInAppendOrder()
    {
        string directory = NewDirectory();
        try
        {
            await Store.AppendLineAsync(directory, FileName, "first", CancellationToken.None);
            await Store.AppendLineAsync(directory, FileName, () => "second", CancellationToken.None);

            IReadOnlyList<string> read = Store.ReadCompleteLines(Path.Combine(directory, FileName));

            Assert.Equal(["first", "second"], read);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AWindowKeepsTheNewestWholeLinesAndReportsTheCut()
    {
        string directory = NewDirectory();
        try
        {
            await Store.AppendLineAsync(directory, FileName, new string('a', 10), CancellationToken.None);
            await Store.AppendLineAsync(directory, FileName, new string('b', 10), CancellationToken.None);
            await Store.AppendLineAsync(directory, FileName, new string('c', 10), CancellationToken.None);

            LogTail tail = Store.ReadTail(Path.Combine(directory, FileName), 15, keepUnterminatedFinalLine: false);

            Assert.True(tail.Truncated);
            Assert.Equal([new string('c', 10)], tail.Lines);
            Assert.Null(tail.Unreadable);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AWindowUnderOneByteIsRefused(long tailBytes)
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, FileName);

            Assert.Throws<ArgumentOutOfRangeException>(() => Store.ReadTail(path, tailBytes, keepUnterminatedFinalLine: false));
            Assert.Throws<ArgumentOutOfRangeException>(() => Store.ReadTailLines(path, tailBytes, keepUnterminatedFinalLine: false));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ALogThatIsNotThereReadsAsEmptyWithNoProblem()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "no-such", FileName);

            LogTail tail = Store.ReadComplete(path);

            Assert.Empty(tail.Lines);
            Assert.False(tail.Truncated);
            Assert.Null(tail.Unreadable);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ALogThatCannotBeOpenedReadsAsEmptyAndSaysWhy()
    {
        string directory = NewDirectory();
        try
        {
            LogTail tail = Store.ReadComplete(UnreadablePath(directory));

            Assert.Empty(tail.Lines);
            Assert.False(tail.Truncated);
            Assert.NotNull(tail.Unreadable);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EveryConcurrentAppendLandsAsOneCompleteLine()
    {
        string directory = NewDirectory();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 25).Select(index =>
                Task.Run(() => Store.AppendLineAsync(directory, FileName, () => $"line-{index}", CancellationToken.None))));

            LogTail tail = Store.ReadComplete(Path.Combine(directory, FileName));

            Assert.False(tail.Truncated);
            Assert.Equal(25, tail.Lines.Count);
            Assert.Equal(
                Enumerable.Range(0, 25).Select(index => $"line-{index}").Order(StringComparer.Ordinal),
                tail.Lines.Order(StringComparer.Ordinal));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }
}
