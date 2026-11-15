using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Incremental session, finding, run, execution, and harness rollup projection.
/// </summary>
/// <remarks>
/// The projection engines short-circuit when nothing changed: a pass over an already-current store
/// reports the stored watermarks rather than rebuilding sessions. Callers may therefore ask for a
/// projection before every read without paying for it on a quiet dashboard.
/// </remarks>
public class CanonProjectionService : ICanonProjectionService
{
    private readonly ICanonStoreRepository store;
    private readonly CanonSessionProjectorEngine projector;
    private readonly CanonRollupsEngine rollups;

    /// <summary>
    /// Initializes a new <see cref="CanonProjectionService"/>.
    /// </summary>
    /// <param name="store">Canonical store repository holding sessions, findings, and rollups.</param>
    /// <param name="projector">Session and finding projector.</param>
    /// <param name="rollups">Run, execution, and harness rollup projector.</param>
    public CanonProjectionService(
        ICanonStoreRepository store,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine rollups)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(rollups);

        this.store = store;
        this.projector = projector;
        this.rollups = rollups;
    }

    /// <inheritdoc />
    public ProjectionPassReport ProjectIncremental()
    {
        if (projector.IsProjectionCurrent())
        {
            return new ProjectionPassReport(
                Built: 0,
                Skipped: store.ListSessions().Count,
                Pruned: 0,
                Findings: 0,
                RebuiltKeys: [],
                SkippedKeys: []);
        }

        return projector.ProjectIncremental();
    }

    /// <inheritdoc />
    public IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses() =>
        rollups.ProjectRunsAndHarnesses();

    /// <inheritdoc />
    public (ProjectionPassReport Projection, IReadOnlyList<HarnessRollupRow> Rollups) ProjectAll()
    {
        ProjectionPassReport projection = ProjectIncremental();
        IReadOnlyList<HarnessRollupRow> harnessRollups = ProjectRunsAndHarnesses();
        return (projection, harnessRollups);
    }
}
