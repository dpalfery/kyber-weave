using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Validated relational-store clean: pause ingestion, wipe the chosen scope, re-project, optionally
/// re-ingest, and always resume.
/// </summary>
/// <remarks>
/// <para>
/// The whole scope is validated before anything is deleted, so a caller that passes a mistyped harness
/// name wipes nothing. Resume happens in a <c>finally</c>: a wipe that throws still returns the
/// receiver to service rather than leaving capture switched off until the next restart.
/// </para>
/// <para>
/// The wipe deletes the scope's collected sources (each source's records cascade away), the projected
/// session, run, execution, rollup, and finding rows, and the write-marker invalidation that makes the
/// projection pass rebuild from what is left. The relational store keeps collected session identity
/// rows — they are harness-owned, not source-owned — so a clean reports zero sessions deleted while
/// every record, projection, and derived row of the scope is gone. Ingest problems and quarantined
/// spans are operator diagnostics, not telemetry, so they are reported as zero deleted as well.
/// </para>
/// </remarks>
public class CleanService : ICleanService
{
    /// <summary>Maximum permitted history lookback window when re-ingesting after a clean.</summary>
    public const int MaxCleanReingestWeeks = 104;

    /// <summary>Reported harness list when the whole store was wiped.</summary>
    public const string AllHarnessesMarker = "*";

    private static readonly IReadOnlySet<string> KnownHarnesses =
        new HashSet<string>(
        [
            "claude",
            "claude-code",
            "claude-cli",
            "claude-desktop",
            "codex",
            "codex-cli",
            "codex-desktop",
            "copilot",
            "copilot-cli",
            "copilot-vscode",
            "copilot-jetbrains",
            "copilot-agent",
            "antigravity",
            "antigravity-cli",
            "antigravity-ide",
            "cursor",
            "cline",
            "cline-cli",
            "pi",
            "opencode",
            "kilo-code",
            "kilo-shared-runtime",
            "kilo-vscode-legacy",
            "droid",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IRelationalStoreReader reader;
    private readonly IRelationalStoreWriter writer;
    private readonly IProjectionStore projections;
    private readonly IOperationalStore operationalStore;
    private readonly ICanonProjectionService projection;
    private readonly ITranscriptIngestionService ingestion;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new <see cref="CleanService"/>.
    /// </summary>
    /// <param name="reader">Relational reader counting the scope before it is wiped.</param>
    /// <param name="writer">Relational writer deleting the scope's collected sources.</param>
    /// <param name="projections">Projection store holding the derived rows the report counts.</param>
    /// <param name="operationalStore">Operational store owning the clean stamp and projection invalidation.</param>
    /// <param name="projection">Projection service rebuilding derived state after the wipe.</param>
    /// <param name="ingestion">Ingestion service used for an optional history backfill.</param>
    /// <param name="timeProvider">Clock supplying the clean stamp; defaults to <see cref="TimeProvider.System"/>.</param>
    public CleanService(
        IRelationalStoreReader reader,
        IRelationalStoreWriter writer,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(ingestion);

        this.reader = reader;
        this.writer = writer;
        this.projections = projections;
        this.operationalStore = operationalStore;
        this.projection = projection;
        this.ingestion = ingestion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CleanReport CleanDatabase(
        CleanScope scope,
        Action? pauseIngestion = null,
        Action? resumeIngestion = null,
        Func<IReadOnlyList<string>?, int, int>? reingest = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        IReadOnlyList<string>? canonicalScope = ValidateScope(scope);
        if (scope.ReingestWeeks is int weeks && (weeks < 1 || weeks > MaxCleanReingestWeeks))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"cleanDatabase: reingestWeeks must be an integer between 1 and {MaxCleanReingestWeeks}, or null"),
                nameof(scope));
        }

        pauseIngestion?.Invoke();
        try
        {
            StoreWipeReport wipe = Wipe(canonicalScope);

            _ = projection.ProjectIncremental();
            _ = projection.ProjectRunsAndHarnesses();

            bool reingested = false;
            if (scope.ReingestWeeks is int backfillWeeks)
            {
                if (reingest is not null)
                {
                    _ = reingest(canonicalScope, backfillWeeks);
                }
                else
                {
                    _ = ingestion.RefreshHarnessSources(backfillWeeks);
                }

                reingested = true;
            }

            IReadOnlyList<string> reportedHarnesses = canonicalScope ?? [AllHarnessesMarker];
            return new CleanReport(
                Harnesses: reportedHarnesses,
                Wipe: wipe,
                Reingested: reingested,
                HistoryWeeks: scope.ReingestWeeks);
        }
        finally
        {
            resumeIngestion?.Invoke();
        }
    }

    /// <summary>Resolves the harness scope, or <see langword="null"/> for a whole-store wipe.</summary>
    /// <param name="scope">Requested clean scope.</param>
    /// <returns>Normalized harness names in ordinal order, or <see langword="null"/> for everything.</returns>
    /// <exception cref="ArgumentException">The scope is ambiguous, empty, or names no known harness.</exception>
    public static IReadOnlyList<string>? ValidateScope(CleanScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.All)
        {
            if (scope.Harnesses is { Count: > 0 })
            {
                throw new ArgumentException("cleanDatabase: ambiguous scope — pass All or Harnesses, not both", nameof(scope));
            }

            return null;
        }

        if (scope.Harnesses is null || scope.Harnesses.Count == 0)
        {
            throw new ArgumentException("cleanDatabase: scope selects nothing — pass All or at least one harness", nameof(scope));
        }

        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (string candidate in scope.Harnesses)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string trimmed = candidate.Trim();
            if (!KnownHarnesses.Contains(trimmed))
            {
                continue;
            }

            known.Add(CanonPricing.NormalizeHarnessName(trimmed));
        }

        if (known.Count == 0)
        {
            throw new ArgumentException("cleanDatabase: scope selects no known harness", nameof(scope));
        }

        return [.. known.Order(StringComparer.Ordinal)];
    }

    private StoreWipeReport Wipe(IReadOnlyList<string>? harnesses)
    {
        string nowIso = timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        WipeScope scope = ReadScope(harnesses);

        // Findings reference sessions without a foreign key, so the clean removes them itself before
        // the rows they point at disappear.
        foreach (string sessionId in scope.FindingSessionIds)
        {
            projections.DeleteFindingsForSession(sessionId);
        }

        foreach (StoredSessionProjection sessionProjection in scope.SessionProjections)
        {
            projections.DeleteSessionProjection(sessionProjection.SessionId);
        }

        foreach (StoredRun run in scope.Runs)
        {
            projections.DeleteRun(run.RunId);
        }

        foreach (StoredHarnessRollup rollup in scope.Rollups)
        {
            projections.DeleteHarnessRollup(rollup.HarnessId);
        }

        foreach (StoredSource source in scope.Sources)
        {
            writer.DeleteSource(source.SourceId);
        }

        // The wipe deletes projection rows directly and may delete no source at all, so the pass
        // that follows would otherwise find the marker where the last pass left it and skip:
        // bumping the marker forces a full pass, and the dirty range is cleared in its favour.
        operationalStore.SetWriteMarker(operationalStore.GetWriteMarker() + 1);
        operationalStore.ClearProjectionDirtyState();
        operationalStore.SetLastCleanAtUtc(nowIso);

        return new StoreWipeReport(
            RecordsDeleted: scope.Records,
            SessionsDeleted: 0,
            TurnsDeleted: scope.Turns,
            RunsDeleted: scope.Runs.Count,
            ExecutionsDeleted: scope.Executions,
            FindingsDeleted: scope.Findings,
            RollupsDeleted: scope.Rollups.Count,
            ProblemsDeleted: 0,
            QuarantineDeleted: 0,
            LastCleanAt: nowIso);
    }

    private WipeScope ReadScope(IReadOnlyList<string>? harnesses)
    {
        List<CollectedSession> sessions = [];
        if (harnesses is null)
        {
            sessions.AddRange(reader.Sessions());
        }
        else
        {
            foreach (string harness in harnesses)
            {
                sessions.AddRange(reader.Sessions(harness));
            }
        }

        int records = 0;
        if (harnesses is null)
        {
            records = reader.CountRecords();
        }
        else
        {
            foreach (string harness in harnesses)
            {
                records += reader.CountRecords(harness);
            }
        }

        List<StoredSessionProjection> sessionProjections = [];
        if (harnesses is null)
        {
            sessionProjections.AddRange(projections.ListSessionProjections());
        }
        else
        {
            foreach (string harness in harnesses)
            {
                sessionProjections.AddRange(projections.ListSessionProjections(harness));
            }
        }

        int turns = 0;
        foreach (StoredSessionProjection sessionProjection in sessionProjections)
        {
            turns += projections.GetSessionTurns(sessionProjection.SessionId).Count;
        }

        List<StoredRun> runs = [];
        if (harnesses is null)
        {
            runs.AddRange(projections.ListRuns());
        }
        else
        {
            foreach (string harness in harnesses)
            {
                runs.AddRange(projections.ListRuns(harness));
            }
        }

        int executions = 0;
        foreach (StoredRun run in runs)
        {
            executions += projections.ListExecutionsForRun(run.RunId).Count;
        }

        HashSet<string> scopeSessionIds = new(sessions.Select(static session => session.SessionKey), StringComparer.Ordinal);
        HashSet<string> scopeRunIds = new(runs.Select(static run => run.RunId), StringComparer.Ordinal);

        int findings = 0;
        HashSet<string> findingSessionIds = new(StringComparer.Ordinal);
        foreach (StoredFinding finding in projections.ListFindings())
        {
            bool inScope = harnesses is null
                || (finding.SessionId is not null && scopeSessionIds.Contains(finding.SessionId))
                || (finding.RunId is not null && scopeRunIds.Contains(finding.RunId));
            if (inScope && finding.SessionId is not null)
            {
                findings++;
                findingSessionIds.Add(finding.SessionId);
            }
        }

        List<StoredHarnessRollup> rollups = projections.ListHarnessRollups()
            .Where(rollup => harnesses is null || harnesses.Contains(rollup.HarnessId, StringComparer.Ordinal))
            .ToList();

        List<StoredSource> sources = [];
        if (harnesses is null)
        {
            sources.AddRange(reader.Sources());
        }
        else
        {
            foreach (string harness in harnesses)
            {
                sources.AddRange(reader.Sources(harness));
            }
        }

        return new WipeScope(
            sessions,
            records,
            sessionProjections,
            turns,
            runs,
            executions,
            findings,
            findingSessionIds,
            rollups,
            sources);
    }

    private sealed record WipeScope(
        List<CollectedSession> Sessions,
        int Records,
        List<StoredSessionProjection> SessionProjections,
        int Turns,
        List<StoredRun> Runs,
        int Executions,
        int Findings,
        HashSet<string> FindingSessionIds,
        List<StoredHarnessRollup> Rollups,
        List<StoredSource> Sources);

}
