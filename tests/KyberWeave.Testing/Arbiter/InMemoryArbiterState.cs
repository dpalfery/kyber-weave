using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Arbiter's stores and the real evaluator over the in-memory file system and log fake, so a test that judges the
/// Arbiter's behaviour reads the records back without a disk, a lock file or a clock outside the test.
/// </summary>
/// <remarks>
/// Every store it hands out shares one file system and one log, as the real stores share one directory. A test that
/// needs the stores at its own directory passes it; <see cref="Directory"/> is the directory a test that only judges
/// the evaluator's answers uses. A test whose subject is what the real files hold still uses
/// <see cref="RealArbiterState"/>.
/// </remarks>
internal sealed class InMemoryArbiterState
{
    /// <summary>Creates the state over <paramref name="files"/>.</summary>
    /// <param name="files">The in-memory file system the stores write to; a new one when null.</param>
    internal InMemoryArbiterState(InMemoryFileSystem? files = null)
    {
        Files = files ?? new InMemoryFileSystem();
        Paths = new FakePathResolver(Files);
    }

    /// <summary>The arbiter state directory a test that only judges the evaluator's answers records under.</summary>
    internal static string Directory => Path.Combine(ArbiterTestRoots.Root, "repo", "artifacts", "arbiter");

    /// <summary>Gets the file system the model-targets table and the run stores read.</summary>
    internal InMemoryFileSystem Files { get; }

    /// <summary>Gets the append-only log every store writes through.</summary>
    internal FakeArbiterLogStore Log { get; } = new();

    /// <summary>Gets the path resolver the stores resolve through.</summary>
    internal FakePathResolver Paths { get; }

    /// <summary>The in-flight ledger.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal InFlightLedger Ledger(string arbiterDirectory) => new(arbiterDirectory, Log);

    /// <summary>Gets the in-flight ledger at <see cref="Directory"/>, as its concrete type.</summary>
    internal InFlightLedger LedgerStore => Ledger(Directory);

    /// <summary>The decision log.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    internal DecisionLog Decisions(string arbiterDirectory) => new(arbiterDirectory, Log);

    /// <summary>Gets the decision log at <see cref="Directory"/>.</summary>
    internal DecisionLog DecisionLog => Decisions(Directory);

    /// <summary>Gets every decision recorded at <see cref="Directory"/> so far, in append order.</summary>
    internal IReadOnlyList<ArbiterDecisionRecord> Records => DecisionLog.ReadAll();

    /// <summary>The review, failure and return stores.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    /// <param name="newestItemTime">Reads when a sibling directory was last written; the file system when null.</param>
    internal ArbiterRunStore RunStore(
        string arbiterDirectory,
        ArbiterStoreOptions? options = null,
        TimeProvider? clock = null,
        Func<string, DateTime>? newestItemTime = null) =>
        new(arbiterDirectory, Log, Files, Files, Paths, options, clock, newestItemTime);

    /// <summary>Builds the run store at <see cref="Directory"/>.</summary>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; a fixed clock when null.</param>
    internal ArbiterRunStore RunStore(ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
        RunStore(Directory, options, clock ?? new ArbiterEvaluatorFixtures.Clock(ArbiterEvaluatorFixtures.FixedNow));

    /// <summary>The state factory.</summary>
    internal FileArbiterStateFactory Factory() => new(Log, Files, Files, Paths);

    /// <summary>Gets the state factory the evaluator opens the stores at <see cref="Directory"/> through.</summary>
    internal IArbiterStateFactory State => Factory();

    /// <summary>The gate report reader over the repository in <see cref="Files"/>.</summary>
    internal GateReportFacts GateReports() =>
        new(
            new InMemoryRepositoryFileStore(Files),
            Paths,
            new FakeProcessEnvironment(
                currentDirectory: ArbiterTestRoots.Under("work"),
                isFileSystemCaseInsensitive: !Files.CaseSensitive));

    /// <summary>The plan facts reader over the plans in <see cref="Files"/>.</summary>
    internal PlanFacts PlanFacts() =>
        new(
            new PlanPathResolver(Paths, new FakeProcessEnvironment(currentDirectory: ArbiterTestRoots.Under("work"))),
            Files);

    /// <summary>Builds the real evaluator over these seams, recording at <see cref="Directory"/>.</summary>
    /// <param name="provider">The provider a test scripts.</param>
    /// <param name="secondaryTriggers">Chooses the secondary triggers an event raises; the product's own when null.</param>
    /// <param name="planReader">Supplies plan facts; a pass-through when null.</param>
    /// <param name="gitFacts">Supplies git facts; a pass-through when null.</param>
    /// <param name="clock">The clock latency and retention are measured on; a fixed clock when null.</param>
    /// <param name="modelTargets">Reads the model-targets table; the file-backed reader over <see cref="Files"/> when null.</param>
    internal ArbiterEvaluator Evaluator(
        IArbiterProvider provider,
        Func<string, ArbiterEvent, IReadOnlyList<string>>? secondaryTriggers = null,
        IArbiterPlanReader? planReader = null,
        IArbiterGitFacts? gitFacts = null,
        TimeProvider? clock = null,
        IArbiterModelTargetsReader? modelTargets = null) =>
        new(
            provider,
            _ => null,
            LedgerStore,
            DecisionLog,
            gitFacts ?? new ArbiterEvaluatorFixtures.PassThroughGitFacts(),
            planReader ?? new ArbiterEvaluatorFixtures.PlanFacts(),
            modelTargets ?? new FileArbiterModelTargetsReader(Files),
            GateReports(),
            State,
            clock ?? new ArbiterEvaluatorFixtures.Clock(ArbiterEvaluatorFixtures.FixedNow),
            new FakeIdGenerator(),
            secondaryTriggers);
}
