using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the Arbiter ledger, decision log and run store over the in-memory file system and log fake, so a test whose
/// subject is what the stores hold needs no disk. Every store it hands out shares one file system and one log, as the
/// real stores share one directory.
/// </summary>
/// <param name="files">The in-memory file system the stores write to.</param>
internal sealed class InMemoryArbiterState(InMemoryFileSystem files)
{
    /// <summary>Gets the append-only log every store writes through.</summary>
    internal FakeArbiterLogStore Log { get; } = new();

    /// <summary>Gets the path resolver the stores resolve through.</summary>
    internal FakePathResolver Paths { get; } = new(files);

    /// <summary>The in-flight ledger.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal InFlightLedger Ledger(string arbiterDirectory) => new(arbiterDirectory, Log);

    /// <summary>The decision log.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal DecisionLog Decisions(string arbiterDirectory) => new(arbiterDirectory, Log);

    /// <summary>The review, failure and return stores.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    internal ArbiterRunStore RunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
        new(arbiterDirectory, Log, files, files, Paths, options, clock);

    /// <summary>The state factory.</summary>
    internal FileArbiterStateFactory Factory() => new(Log, files, files, Paths);

    /// <summary>The gate report reader over the repository in <paramref name="files"/>.</summary>
    internal GateReportFacts GateReports() =>
        new(
            new InMemoryRepositoryFileStore(files),
            Paths,
            new FakeProcessEnvironment(
                currentDirectory: ArbiterTestRoots.Under("work"),
                isFileSystemCaseInsensitive: !files.CaseSensitive));
}
