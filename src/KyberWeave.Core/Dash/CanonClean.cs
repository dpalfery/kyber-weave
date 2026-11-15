using System.Globalization;

namespace KyberWeave.Core.Dash;

/// <summary>Requested clean scope and optional history backfill window.</summary>
public sealed record CleanScope(
    bool All = false,
    IReadOnlyList<string>? Harnesses = null,
    int? ReingestWeeks = null);

/// <summary>Result summary returned after a validated database clean.</summary>
public sealed record CleanReport(
    IReadOnlyList<string> Harnesses,
    StoreWipeReport Wipe,
    bool Reingested,
    int? HistoryWeeks);

/// <summary>
/// Central database clean orchestrator (<c>dash/src/clean/clean.ts</c>): pauses ingestion,
/// wipes the validated scope, re-projects derived state, optionally triggers re-ingestion, and
/// always resumes ingestion in a <c>finally</c> block.
/// </summary>
public static class CanonClean
{
    /// <summary>Maximum permitted lookback window when re-ingesting history after a clean.</summary>
    public const int MaxCleanReingestWeeks = 104;

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

    /// <summary>
    /// Resolves and validates <paramref name="scope"/> before touching <paramref name="store"/>,
    /// then performs the pause -> wipe -> project -> optional reingest -> resume lifecycle.
    /// </summary>
    public static CleanReport CleanDatabase(
        CanonStore store,
        CleanScope scope,
        Action? pauseIngestion = null,
        Action? resumeIngestion = null,
        Func<IReadOnlyList<string>?, int, int>? reingest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
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

            CanonSessionProjector.ProjectIncremental(store);
            CanonRollups.ProjectRunsAndHarnesses(store);

            bool reingested = false;
            if (scope.ReingestWeeks is int backfillWeeks && reingest is not null)
            {
                reingest(canonicalScope, backfillWeeks);
                reingested = true;
            }

            IReadOnlyList<string> reportedHarnesses = canonicalScope ?? ["*"];
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

    private static IReadOnlyList<string>? ValidateScope(CleanScope scope)
    {
        if (scope.All)
        {
            if (scope.Harnesses is { Count: > 0 })
                throw new ArgumentException("cleanDatabase: ambiguous scope — pass All or Harnesses, not both", nameof(scope));

            return null;
        }

        if (scope.Harnesses is null || scope.Harnesses.Count == 0)
            throw new ArgumentException("cleanDatabase: scope selects nothing — pass All or at least one harness", nameof(scope));

        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (string candidate in scope.Harnesses)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string trimmed = candidate.Trim();
            if (!KnownHarnesses.Contains(trimmed))
                continue;

            known.Add(CanonPricing.NormalizeHarnessName(trimmed));
        }

        if (known.Count == 0)
            throw new ArgumentException("cleanDatabase: scope selects no known harness", nameof(scope));

        return [.. known.Order(StringComparer.Ordinal)];
    }
}
