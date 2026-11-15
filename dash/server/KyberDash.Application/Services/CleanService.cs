using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Validated database clean: pause ingestion, wipe the chosen scope, re-project, optionally re-ingest,
/// and always resume.
/// </summary>
/// <remarks>
/// The whole scope is validated before anything is deleted, so a caller that passes a mistyped harness
/// name wipes nothing. Resume happens in a <c>finally</c>: a wipe that throws still returns the
/// receiver to service rather than leaving capture switched off until the next restart.
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
            "aider",
            "opencode",
            "kilo-code",
            "kilo-shared-runtime",
            "kilo-vscode-legacy",
            "droid",
            "windsurf",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ICanonStoreRepository store;
    private readonly ICanonProjectionService projection;
    private readonly ITranscriptIngestionService ingestion;

    /// <summary>
    /// Initializes a new <see cref="CleanService"/>.
    /// </summary>
    /// <param name="store">Canonical store repository performing the wipe.</param>
    /// <param name="projection">Projection service rebuilding derived state after the wipe.</param>
    /// <param name="ingestion">Ingestion service used for an optional history backfill.</param>
    public CleanService(
        ICanonStoreRepository store,
        ICanonProjectionService projection,
        ITranscriptIngestionService ingestion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(ingestion);

        this.store = store;
        this.projection = projection;
        this.ingestion = ingestion;
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
            StoreWipeReport wipe = canonicalScope is null
                ? store.WipeAll()
                : store.WipeHarnesses(canonicalScope);

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
}
