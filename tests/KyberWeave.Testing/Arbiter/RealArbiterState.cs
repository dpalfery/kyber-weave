using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real Arbiter ledger, decision log and run store over the real adapters for the tests whose subject is
/// what the files hold. The classes themselves take their seams, so every test that does not need real disk injects
/// <see cref="FakeArbiterLogStore"/> and the in-memory file system instead.
/// </summary>
internal static class RealArbiterState
{
    /// <summary>The in-flight ledger over the real append lock and the real disk.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal static InFlightLedger Ledger(string arbiterDirectory) => new(arbiterDirectory, Log());

    /// <summary>The decision log over the real append lock and the real disk.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal static DecisionLog Decisions(string arbiterDirectory) => new(arbiterDirectory, Log());

    /// <summary>The review, failure and return stores over the real append lock and the real disk.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    internal static ArbiterRunStore RunStore(
        string arbiterDirectory,
        ArbiterStoreOptions? options = null,
        Func<DateTimeOffset>? clock = null) =>
        new(arbiterDirectory, Log(), Files(), Files(), options, clock);

    /// <summary>The state factory over the real append lock and the real disk.</summary>
    internal static FileArbiterStateFactory Factory() => new(Log(), Files(), Files());

    /// <summary>The run store whose sweep ages a sibling through <paramref name="newestItemTime"/>, for a failing read.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    /// <param name="newestItemTime">Reads when a sibling directory was last written; the file system when null.</param>
    internal static ArbiterRunStore RunStore(
        string arbiterDirectory,
        ArbiterStoreOptions? options,
        Func<DateTimeOffset>? clock,
        Func<string, DateTime>? newestItemTime) =>
        new(arbiterDirectory, Log(), Files(), Files(), options, clock, newestItemTime);

    private static ArbiterLogFiles Log() => new();

    private static IWritableFileSystem Files() => new PhysicalFileSystem();
}
