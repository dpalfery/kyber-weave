using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The real evaluator, ledger, decision log and run store over the in-memory seams, so a test that judges the
/// Arbiter's behaviour reads the records back without a disk, a lock file or a clock outside the test.
/// </summary>
/// <remarks>
/// Every test that used a scratch directory to hold the state uses this instead. A test whose subject is what the
/// files hold still uses <see cref="RealArbiterState"/>.
/// </remarks>
internal sealed class InMemoryArbiterState
{
    /// <summary>The arbiter state directory every record sits under.</summary>
    public const string Directory = "/repo/artifacts/arbiter";

    /// <summary>Gets the log seam every store appends through.</summary>
    public FakeArbiterLogStore Log { get; } = new();

    /// <summary>Gets the file system the model-targets table and the run stores read.</summary>
    public InMemoryFileSystem Files { get; } = new();

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    public IPathResolver Paths => new FakePathResolver(Files);

    /// <summary>Gets the in-flight ledger the evaluator pairs pre and post events through.</summary>
    public IArbiterLedger Ledger => LedgerStore;

    /// <summary>Gets the in-flight ledger as its concrete type, for a test that seeds events through it.</summary>
    public InFlightLedger LedgerStore => new(Directory, Log);

    /// <summary>Gets the decision log the evaluator records each decision in.</summary>
    public IArbiterDecisionLog Decisions => new DecisionLog(Directory, Log);

    /// <summary>Gets every decision recorded so far, in append order.</summary>
    public IReadOnlyList<ArbiterDecisionRecord> Records => Decisions.ReadAll();

    /// <summary>Gets the gate-report facts the evaluator reads over <see cref="Files"/>.</summary>
    public IGateReportFacts GateReports => new GateReportFacts(
        new InMemoryRepositoryFileStore(Files),
        new FakePathResolver(Files),
        new FakeProcessEnvironment(currentDirectory: "/repo"));

    /// <summary>Gets the state factory the evaluator opens its stores through.</summary>
    public IArbiterStateFactory State => new FileArbiterStateFactory(Log, Files, Files, new FakePathResolver(Files));

    /// <summary>Builds the real evaluator over these seams.</summary>
    /// <param name="provider">The provider a test scripts.</param>
    /// <param name="secondaryTriggers">Chooses the secondary triggers an event raises; the product's own when null.</param>
    /// <param name="planReader">Supplies plan facts; a pass-through when null.</param>
    /// <param name="gitFacts">Supplies git facts; a pass-through when null.</param>
    /// <param name="clock">The clock latency and retention are measured on; a fixed clock when null.</param>
    /// <param name="modelTargets">Reads the model-targets table; the file-backed reader over <see cref="Files"/> when null.</param>
    public ArbiterEvaluator Evaluator(
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
            Decisions,
            gitFacts ?? new ArbiterEvaluatorFixtures.PassThroughGitFacts(),
            planReader ?? new ArbiterEvaluatorFixtures.PlanFacts(),
            modelTargets ?? new FileArbiterModelTargetsReader(Files),
            GateReports,
            State,
            clock ?? new ArbiterEvaluatorFixtures.Clock(ArbiterEvaluatorFixtures.FixedNow),
            new FakeIdGenerator(),
            secondaryTriggers);

    /// <summary>Builds the real run store over these seams.</summary>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; a fixed clock when null.</param>
    public ArbiterRunStore RunStore(ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
        new(Directory, Log, Files, Files, Paths, options, clock ?? new ArbiterEvaluatorFixtures.Clock(ArbiterEvaluatorFixtures.FixedNow));
}
