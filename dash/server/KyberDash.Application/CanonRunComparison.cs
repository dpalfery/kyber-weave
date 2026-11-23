using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>One resolved run turn the bridge hands the comparison, with its canonical figures.</summary>
/// <param name="TurnIndex">Zero-based position of the turn within its run.</param>
/// <param name="SpanId">Canonical record/span identifier.</param>
/// <param name="FreshInput">Measured fresh input tokens.</param>
/// <param name="CacheRead">Measured cache-read tokens.</param>
/// <param name="CacheCreation">Measured cache-creation tokens.</param>
/// <param name="Output">Measured output tokens.</param>
/// <param name="ReportedInput">Harness-reported input total, when the harness reported one.</param>
/// <param name="ReportedOutput">Harness-reported output total, when the harness reported one.</param>
/// <param name="All">Canonical total when measurable; <see langword="null"/> when withheld.</param>
/// <param name="AllReason">Why <paramref name="All"/> was withheld; <see langword="null"/> alongside a measured total.</param>
/// <param name="Cost">Read-time published cost summary, when the turn carries one.</param>
/// <param name="Tools">Tool names the content-free record read attributes to this turn.</param>
public record RunComparisonTurn(
    int TurnIndex,
    string? SpanId,
    long? FreshInput,
    long? CacheRead,
    long? CacheCreation,
    long? Output,
    long? ReportedInput,
    long? ReportedOutput,
    long? All,
    string? AllReason,
    ReadTimeCostSummary? Cost,
    IReadOnlyList<string> Tools);

/// <summary>One run side handed to <see cref="CanonRunComparison.Compare"/>: identity and resolved turns only.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Harness">Harness identifier.</param>
/// <param name="Label">Run label, when one was derived.</param>
/// <param name="Turns">The run's resolved turns, in execution order.</param>
public record RunComparisonRun(
    string RunId,
    string Harness,
    string? Label,
    IReadOnlyList<RunComparisonTurn> Turns);

/// <summary>
/// Phase-aligned comparison of two canon runs — the payload <c>GET /api/kyber/compare/runs</c>
/// serves. Turn token totals, phase alignment, per-phase summaries, aggregate totals, and the
/// verdict are decided here, once, so no surface re-derives them (the KyberDash display-layer rule).
/// </summary>
/// <remarks>
/// <para>
/// A turn's canonical token total is the read model's measured input plus output — the same figure
/// the run detail page serves as a turn's <c>tokens</c>. When either side of that sum was not
/// measured the total is withheld and the turn carries <c>tokens.allReason</c> instead: an absent
/// total is a different state from a measured zero, and an unmeasured turn never contributes a
/// fabricated zero to any aggregate it would have polluted.
/// </para>
/// <para>
/// Phases are inferred from the tool names the content-free record read attributes to each turn,
/// checked in the order verification, resolution, implementation, exploration, and fall back to
/// turn position when a turn carries no tool evidence: the last turn resolves, the opening third
/// explores, and a turn with no evidence carries the phase before it. This is the retired
/// TypeScript engine's <c>inferTurnPhase</c> rule, so comparisons stay comparable across the
/// engine migration.
/// </para>
/// <para>
/// Recommendation history is never caller-supplied on the public route (issue #190): the verdict
/// omits <c>completedPairCount</c>, declares <c>historyAvailability: "unavailable"</c>, and keeps
/// <c>canPromote: false</c> until a store-backed measurement exists. The relational read model
/// keeps no outcome rows either, so the verdict never claims an outcome regression it did not
/// observe and simply omits <c>outcomeRegression</c>.
/// </para>
/// </remarks>
public static class CanonRunComparison
{
    /// <summary>Explicit not-measurable marker a turn carries when its canonical total is withheld.</summary>
    public const string TurnTokensNotMeasurableReason = "token telemetry unavailable for this turn";

    private static readonly string[] TaskPhases = ["exploration", "implementation", "verification", "resolution"];

    private static readonly HashSet<string> ExplorationTools =
    [
        "READ_FILE", "VIEW_FILE", "LIST_DIR", "FIND_BY_NAME", "GREP_SEARCH", "GLOB",
        "SEARCH", "READ", "CAT", "LS", "FIND", "LOCATE", "INSPECT", "FETCH", "GET",
    ];

    private static readonly HashSet<string> ImplementationTools =
    [
        "WRITE_TO_FILE", "REPLACE_FILE_CONTENT", "EDIT_FILE", "CREATE_FILE", "PATCH",
        "EDIT", "WRITE", "APPLY_PATCH", "MODIFY", "UPDATE",
    ];

    private static readonly HashSet<string> VerificationTools =
    [
        "RUN_TESTS", "TEST", "VITEST", "PYTEST", "JEST", "CYPRESS", "PLAYWRIGHT",
        "TYPECHECK", "TSC", "LINT", "ESLINT", "CHECK", "VALIDATE",
    ];

    private static readonly HashSet<string> ResolutionTools =
    [
        "GIT_COMMIT", "GIT_PUSH", "TASK_COMPLETE", "FINISH", "RESOLVE", "COMPLETE", "SUBMIT",
    ];

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>One run turn with the phase the comparison inferred for it.</summary>
    private sealed record PhaseTurn(RunComparisonTurn Turn, string Phase);

    /// <summary>Token and cost aggregate over one run's turns, or one phase's turns.</summary>
    private sealed record TurnAggregate(
        int Turns,
        long Tokens,
        double? Cost,
        string? UnavailableReason);

    /// <summary>
    /// Compares two resolved runs and answers the full <c>compare/runs</c> payload: measured or
    /// withheld run sides, phase-aligned turn pairs with per-pair signals and readings, per-phase
    /// summaries, aggregate totals, and the never-promoted public verdict.
    /// </summary>
    /// <param name="runA">Baseline run.</param>
    /// <param name="runB">Comparison run.</param>
    /// <returns>The comparison payload JSON element.</returns>
    public static JsonElement Compare(RunComparisonRun runA, RunComparisonRun runB)
    {
        ArgumentNullException.ThrowIfNull(runA);
        ArgumentNullException.ThrowIfNull(runB);

        List<PhaseTurn> turnsA = InferPhases(runA.Turns);
        List<PhaseTurn> turnsB = InferPhases(runB.Turns);

        List<object> pairs = BuildPairs(turnsA, turnsB);
        Dictionary<string, object> phaseSummaries = BuildPhaseSummaries(turnsA, turnsB);

        TurnAggregate aggregateA = AggregateTurns(turnsA);
        TurnAggregate aggregateB = AggregateTurns(turnsB);

        (bool costComparable, string? costRefusalReason) = CostComparability(turnsA, turnsB);
        string? totalsReason = TotalsUnavailableReason(
            runA.RunId,
            runB.RunId,
            aggregateA,
            aggregateB);
        object totals = totalsReason is null
            ? BuildMeasuredTotals(aggregateA, aggregateB, costComparable, costRefusalReason)
            : BuildUnavailableTotals(totalsReason, aggregateA, aggregateB, costComparable, costRefusalReason);

        return JsonSerializer.SerializeToElement(new
        {
            runA = BuildRunSide(runA, aggregateA),
            runB = BuildRunSide(runB, aggregateB),
            pairs,
            phaseSummaries,
            totals,
            verdict = BuildVerdict(runA.RunId, runB.RunId, pairs.Count),
        }, CamelCaseJsonOptions);
    }

    /// <summary>Sums one turn collection's measured tokens and priced costs, naming the first coverage gap.</summary>
    /// <param name="turns">The turns being aggregated.</param>
    /// <returns>The collection's token and cost aggregate.</returns>
    private static TurnAggregate AggregateTurns(IReadOnlyList<PhaseTurn> turns)
    {
        int count = 0;
        long tokens = 0;
        double? cost = null;
        string? unavailableReason = null;
        foreach (PhaseTurn turn in turns)
        {
            count++;
            if (turn.Turn.All.HasValue)
            {
                tokens += turn.Turn.All.Value;
            }
            else
            {
                unavailableReason ??= turn.Turn.AllReason ?? TurnTokensNotMeasurableReason;
            }

            double? turnCost = UsdCost(turn.Turn.Cost);
            if (turnCost.HasValue)
            {
                cost = (cost ?? 0) + turnCost.Value;
            }
        }

        return new TurnAggregate(count, tokens, cost, unavailableReason);
    }

    /// <summary>Infers each turn's phase, carrying the previous turn's phase as the no-evidence fallback.</summary>
    /// <param name="turns">The run's resolved turns.</param>
    /// <returns>The turns with their inferred phases, in order.</returns>
    private static List<PhaseTurn> InferPhases(IReadOnlyList<RunComparisonTurn> turns)
    {
        List<PhaseTurn> phased = new(turns.Count);
        string? previousPhase = null;
        for (int index = 0; index < turns.Count; index++)
        {
            string phase = InferTurnPhase(turns[index], index, turns.Count, previousPhase);
            phased.Add(new PhaseTurn(turns[index], phase));
            previousPhase = phase;
        }

        return phased;
    }

    /// <summary>
    /// Infers one turn's phase from its tool evidence, falling back to its position: the last turn
    /// of a multi-turn run resolves, the opening third explores, and a turn with no evidence
    /// carries the phase before it.
    /// </summary>
    /// <param name="turn">The turn being classified.</param>
    /// <param name="turnIndex">Zero-based position of the turn.</param>
    /// <param name="totalTurns">Turns in the run.</param>
    /// <param name="previousPhase">The phase inferred for the turn before this one.</param>
    /// <returns>The inferred phase name.</returns>
    private static string InferTurnPhase(
        RunComparisonTurn turn,
        int turnIndex,
        int totalTurns,
        string? previousPhase)
    {
        List<string> tools = [.. turn.Tools.Select(static tool => tool.ToUpperInvariant())];

        if (tools.Any(VerificationTools.Contains))
        {
            return "verification";
        }

        if (tools.Any(ResolutionTools.Contains))
        {
            return "resolution";
        }

        if (tools.Any(ImplementationTools.Contains))
        {
            return "implementation";
        }

        if (tools.Any(ExplorationTools.Contains))
        {
            return "exploration";
        }

        if (totalTurns > 1 && turnIndex == totalTurns - 1)
        {
            return "resolution";
        }

        if (totalTurns > 2 && turnIndex < Math.Max(1, (int)Math.Ceiling(totalTurns * 0.35)))
        {
            return "exploration";
        }

        return previousPhase ?? "exploration";
    }

    /// <summary>Aligns the two runs' turns by phase in semantic order, pairing slot by slot.</summary>
    /// <param name="turnsA">Run A's phased turns.</param>
    /// <param name="turnsB">Run B's phased turns.</param>
    /// <returns>The aligned pair payload objects, phase by phase.</returns>
    private static List<object> BuildPairs(List<PhaseTurn> turnsA, List<PhaseTurn> turnsB)
    {
        List<object> pairs = [];
        foreach (string phase in TaskPhases)
        {
            List<PhaseTurn> phaseTurnsA = [.. turnsA.Where(turn => string.Equals(turn.Phase, phase, StringComparison.Ordinal))];
            List<PhaseTurn> phaseTurnsB = [.. turnsB.Where(turn => string.Equals(turn.Phase, phase, StringComparison.Ordinal))];
            int slots = Math.Max(phaseTurnsA.Count, phaseTurnsB.Count);
            for (int slot = 0; slot < slots; slot++)
            {
                PhaseTurn? turnA = slot < phaseTurnsA.Count ? phaseTurnsA[slot] : null;
                PhaseTurn? turnB = slot < phaseTurnsB.Count ? phaseTurnsB[slot] : null;
                pairs.Add(new
                {
                    phase,
                    phaseIndex = slot,
                    runATurn = turnA is null ? null : BuildTurnDto(turnA),
                    runBTurn = turnB is null ? null : BuildTurnDto(turnB),
                    signals = BuildTurnSignals(turnA, turnB),
                    reading = BuildTurnReading(phase, turnA, turnB),
                });
            }
        }

        return pairs;
    }

    /// <summary>Shapes one paired turn for the payload: canonical figures, the withheld-total marker, and its tool evidence.</summary>
    /// <param name="turn">The turn being shaped.</param>
    /// <returns>The turn's payload dictionary.</returns>
    private static Dictionary<string, object?> BuildTurnDto(PhaseTurn turn)
    {
        Dictionary<string, object?> tokens = new(StringComparer.Ordinal)
        {
            ["freshInput"] = turn.Turn.FreshInput,
            ["cacheRead"] = turn.Turn.CacheRead,
            ["cacheCreation"] = turn.Turn.CacheCreation,
            ["output"] = turn.Turn.Output,
            ["reportedInput"] = turn.Turn.ReportedInput,
            ["reportedOutput"] = turn.Turn.ReportedOutput,
        };

        if (turn.Turn.All.HasValue)
        {
            tokens["all"] = turn.Turn.All.Value;
        }
        else
        {
            tokens["allReason"] = turn.Turn.AllReason ?? TurnTokensNotMeasurableReason;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["turnIndex"] = turn.Turn.TurnIndex,
            ["spanId"] = turn.Turn.SpanId,
            ["phase"] = turn.Phase,
            ["tokens"] = tokens,
            ["cost"] = turn.Turn.Cost,
            ["tools"] = turn.Turn.Tools,
        };
    }

    /// <summary>
    /// Compares the two turns of one aligned slot. Unmeasurable signals render
    /// <c>not_comparable</c> with a reason rather than a delta of zero.
    /// </summary>
    /// <param name="turnA">Run A's turn in the slot, or <see langword="null"/> when it had none.</param>
    /// <param name="turnB">Run B's turn in the slot, or <see langword="null"/> when it had none.</param>
    /// <returns>The slot's signal payload dictionaries.</returns>
    private static List<Dictionary<string, object?>> BuildTurnSignals(PhaseTurn? turnA, PhaseTurn? turnB)
    {
        List<Dictionary<string, object?>> signals = [];

        // 1. Total tokens.
        if (turnA is not null && turnB is not null)
        {
            if (turnA.Turn.All.HasValue && turnB.Turn.All.HasValue)
            {
                signals.Add(Signal(
                    "total_tokens",
                    "Total tokens",
                    "tokens",
                    turnA.Turn.All.Value,
                    turnB.Turn.All.Value,
                    turnB.Turn.All.Value - turnA.Turn.All.Value,
                    "compared"));
            }
            else
            {
                signals.Add(Signal(
                    "total_tokens",
                    "Total tokens",
                    "tokens",
                    status: "not_comparable",
                    reason: "Token telemetry unavailable in one or both paired turns"));
            }
        }
        else if (turnA is not null)
        {
            Dictionary<string, object?> signal = Signal(
                "total_tokens",
                "Total tokens",
                "tokens",
                status: "missing_in_b",
                reason: "Turn missing in Run B for this phase slot");
            if (turnA.Turn.All.HasValue)
            {
                signal["runAValue"] = turnA.Turn.All.Value;
            }

            signals.Add(signal);
        }
        else if (turnB is not null)
        {
            Dictionary<string, object?> signal = Signal(
                "total_tokens",
                "Total tokens",
                "tokens",
                status: "missing_in_a",
                reason: "Turn missing in Run A for this phase slot");
            if (turnB.Turn.All.HasValue)
            {
                signal["runBValue"] = turnB.Turn.All.Value;
            }

            signals.Add(signal);
        }

        if (turnA is null || turnB is null)
        {
            return signals;
        }

        // 2. Fresh input tokens.
        if (turnA.Turn.FreshInput.HasValue && turnB.Turn.FreshInput.HasValue)
        {
            signals.Add(Signal(
                "fresh_input",
                "Fresh input",
                "tokens",
                turnA.Turn.FreshInput.Value,
                turnB.Turn.FreshInput.Value,
                turnB.Turn.FreshInput.Value - turnA.Turn.FreshInput.Value,
                "compared"));
        }
        else
        {
            signals.Add(Signal(
                "fresh_input",
                "Fresh input",
                "tokens",
                status: "not_comparable",
                reason: "Fresh input telemetry unavailable in one of the paired turns"));
        }

        // 3. Cache-read tokens.
        if (turnA.Turn.CacheRead.HasValue && turnB.Turn.CacheRead.HasValue)
        {
            signals.Add(Signal(
                "cache_read",
                "Cache-read tokens",
                "tokens",
                turnA.Turn.CacheRead.Value,
                turnB.Turn.CacheRead.Value,
                turnB.Turn.CacheRead.Value - turnA.Turn.CacheRead.Value,
                "compared"));
        }
        else
        {
            signals.Add(Signal(
                "cache_read",
                "Cache-read tokens",
                "tokens",
                status: "not_comparable",
                reason: "Cache-read tokens not reported in turn telemetry"));
        }

        // 4. Cost — compared only through one declared basis and currency.
        ReadTimeCostSummary? costA = turnA.Turn.Cost;
        ReadTimeCostSummary? costB = turnB.Turn.Cost;
        if (costA?.Status == CostStatus.Priced
            && costB?.Status == CostStatus.Priced
            && costA.Value is double valueA
            && costB.Value is double valueB)
        {
            if (costA.Basis == costB.Basis && string.Equals(costA.Currency, costB.Currency, StringComparison.Ordinal))
            {
                signals.Add(Signal(
                    "cost",
                    "Cost",
                    costA.Currency ?? "USD",
                    valueA,
                    valueB,
                    valueB - valueA,
                    "compared"));
            }
            else
            {
                signals.Add(Signal(
                    "cost",
                    "Cost",
                    status: "not_comparable",
                    reason: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Cost bases or currencies differ ({WireBasis(costA.Basis)} vs {WireBasis(costB.Basis)})")));
            }
        }

        return signals;
    }

    /// <summary>Builds one signal payload dictionary, omitting every field the comparison did not measure.</summary>
    /// <param name="name">Machine signal key.</param>
    /// <param name="label">Human signal label.</param>
    /// <param name="unit">Optional signal unit.</param>
    /// <param name="runAValue">Run A's measured value.</param>
    /// <param name="runBValue">Run B's measured value.</param>
    /// <param name="delta">Measured delta.</param>
    /// <param name="status">Comparison status.</param>
    /// <param name="reason">Why the signal is not comparable, when it is not.</param>
    /// <returns>The signal payload dictionary.</returns>
    private static Dictionary<string, object?> Signal(
        string name,
        string label,
        string? unit = null,
        double? runAValue = null,
        double? runBValue = null,
        double? delta = null,
        string? status = null,
        string? reason = null)
    {
        Dictionary<string, object?> signal = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["label"] = label,
            ["status"] = status ?? "compared",
        };

        if (unit is not null)
        {
            signal["unit"] = unit;
        }

        if (runAValue.HasValue)
        {
            signal["runAValue"] = runAValue.Value;
        }

        if (runBValue.HasValue)
        {
            signal["runBValue"] = runBValue.Value;
        }

        if (delta.HasValue)
        {
            signal["delta"] = delta.Value;
        }

        if (reason is not null)
        {
            signal["reason"] = reason;
        }

        return signal;
    }

    /// <summary>Authors the one-line reading a paired slot renders — measured figures or the honest gap.</summary>
    /// <param name="phase">The slot's phase.</param>
    /// <param name="turnA">Run A's turn in the slot, or <see langword="null"/>.</param>
    /// <param name="turnB">Run B's turn in the slot, or <see langword="null"/>.</param>
    /// <returns>The reading text.</returns>
    private static string BuildTurnReading(string phase, PhaseTurn? turnA, PhaseTurn? turnB)
    {
        string toolsA = turnA is null ? "" : ToolsSuffix(turnA.Turn.Tools);
        string toolsB = turnB is null ? "" : ToolsSuffix(turnB.Turn.Tools);

        if (turnA is not null && turnB is not null)
        {
            if (!turnA.Turn.All.HasValue || !turnB.Turn.All.HasValue)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Phase {phase} (turn {turnA.Turn.TurnIndex + 1} vs turn {turnB.Turn.TurnIndex + 1}): Run A {FormatTokens(turnA.Turn.All)}{toolsA}; Run B {FormatTokens(turnB.Turn.All)}{toolsB}. Token delta unavailable.");
            }

            long delta = turnB.Turn.All.Value - turnA.Turn.All.Value;
            string deltaText = delta >= 0 ? "+" + FormatTokens(delta) : FormatTokens(delta);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Phase {phase} (turn {turnA.Turn.TurnIndex + 1} vs turn {turnB.Turn.TurnIndex + 1}): Run A used {FormatTokens(turnA.Turn.All.Value)} tokens{toolsA}; Run B used {FormatTokens(turnB.Turn.All.Value)} tokens{toolsB}. Delta: {deltaText} tokens.");
        }

        if (turnA is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Phase {phase} (turn {turnA.Turn.TurnIndex + 1}): Run A executed {FormatTokens(turnA.Turn.All)}{toolsA}; Run B required no turns in this phase slot (completed phase faster).");
        }

        if (turnB is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Phase {phase} (turn {turnB.Turn.TurnIndex + 1}): Run B executed {FormatTokens(turnB.Turn.All)}{toolsB}; Run A had no corresponding turn in this phase slot (omitted or completed faster).");
        }

        return string.Create(CultureInfo.InvariantCulture, $"Phase {phase}: No turns recorded in either run.");
    }

    /// <summary>Builds the per-phase summary rows, withholding token sums a single unmeasured turn would poison.</summary>
    /// <param name="turnsA">Run A's phased turns.</param>
    /// <param name="turnsB">Run B's phased turns.</param>
    /// <returns>One summary dictionary per phase, keyed by phase name.</returns>
    private static Dictionary<string, object> BuildPhaseSummaries(List<PhaseTurn> turnsA, List<PhaseTurn> turnsB)
    {
        Dictionary<string, object> summaries = new(StringComparer.Ordinal);
        foreach (string phase in TaskPhases)
        {
            List<PhaseTurn> phaseTurnsA = [.. turnsA.Where(turn => string.Equals(turn.Phase, phase, StringComparison.Ordinal))];
            List<PhaseTurn> phaseTurnsB = [.. turnsB.Where(turn => string.Equals(turn.Phase, phase, StringComparison.Ordinal))];
            summaries[phase] = BuildPhaseSummary(phase, AggregateTurns(phaseTurnsA), AggregateTurns(phaseTurnsB));
        }

        return summaries;
    }

    /// <summary>Shapes one phase's summary row with its costs and reading.</summary>
    /// <param name="phase">Phase name.</param>
    /// <param name="aggregateA">Run A's aggregate over the phase.</param>
    /// <param name="aggregateB">Run B's aggregate over the phase.</param>
    /// <returns>The summary payload dictionary.</returns>
    private static Dictionary<string, object?> BuildPhaseSummary(
        string phase,
        TurnAggregate aggregateA,
        TurnAggregate aggregateB)
    {
        Dictionary<string, object?> summary = new(StringComparer.Ordinal)
        {
            ["phase"] = phase,
            ["turnsA"] = aggregateA.Turns,
            ["turnsB"] = aggregateB.Turns,
        };

        if (aggregateA.Cost.HasValue)
        {
            summary["costA"] = aggregateA.Cost.Value;
        }

        if (aggregateB.Cost.HasValue)
        {
            summary["costB"] = aggregateB.Cost.Value;
        }

        if (aggregateA.Cost.HasValue && aggregateB.Cost.HasValue)
        {
            summary["costDelta"] = aggregateB.Cost.Value - aggregateA.Cost.Value;
        }

        string? gap = aggregateA.UnavailableReason ?? aggregateB.UnavailableReason;
        string reading;
        if (gap is not null)
        {
            summary["tokensUnavailableReason"] = gap;
            reading = string.Create(
                CultureInfo.InvariantCulture,
                $"Phase {phase}: {aggregateA.Turns} turns in Run A; {aggregateB.Turns} turns in Run B. Tokens unavailable ({gap}).");
        }
        else
        {
            summary["tokensA"] = aggregateA.Tokens;
            summary["tokensB"] = aggregateB.Tokens;
            summary["tokenDelta"] = aggregateB.Tokens - aggregateA.Tokens;
            if (aggregateA.Turns == 0 && aggregateB.Turns > 0)
            {
                reading = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Phase {phase}: Omitted in Run A; Run B executed {aggregateB.Turns} turns ({FormatTokens(aggregateB.Tokens)} tokens).");
            }
            else if (aggregateB.Turns == 0 && aggregateA.Turns > 0)
            {
                reading = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Phase {phase}: Executed {aggregateA.Turns} turns in Run A ({FormatTokens(aggregateA.Tokens)} tokens); omitted in Run B.");
            }
            else
            {
                reading = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Phase {phase}: {aggregateA.Turns} turns in Run A ({FormatTokens(aggregateA.Tokens)} tokens); {aggregateB.Turns} turns in Run B ({FormatTokens(aggregateB.Tokens)} tokens).");
            }
        }

        summary["reading"] = reading;
        return summary;
    }

    /// <summary>Builds the measured run side, or the withheld side naming why, for one run.</summary>
    /// <param name="run">The run being summarized.</param>
    /// <param name="aggregate">The run's token and cost aggregate.</param>
    /// <returns>The side payload dictionary.</returns>
    private static Dictionary<string, object?> BuildRunSide(RunComparisonRun run, TurnAggregate aggregate)
    {
        Dictionary<string, object?> side = new(StringComparer.Ordinal)
        {
            ["runId"] = run.RunId,
            ["harness"] = run.Harness,
        };

        if (run.Label is not null)
        {
            side["label"] = run.Label;
        }

        if (aggregate.Cost.HasValue)
        {
            side["totalCost"] = aggregate.Cost.Value;
        }

        if (aggregate.Turns == 0)
        {
            string reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Run {run.RunId} resolved no comparable model turns for phase alignment.");
            side["availability"] = "unavailable";
            side["reason"] = reason;
            side["metricsReason"] = reason;
            return side;
        }

        if (aggregate.UnavailableReason is not null)
        {
            side["availability"] = "unavailable";
            side["reason"] = aggregate.UnavailableReason;
            side["metricsReason"] = aggregate.UnavailableReason;
            side["turnCount"] = aggregate.Turns;
            return side;
        }

        side["availability"] = "measured";
        side["totalTokens"] = aggregate.Tokens;
        side["turnCount"] = aggregate.Turns;
        return side;
    }

    /// <summary>Builds the measured aggregate totals row for the pair.</summary>
    /// <param name="aggregateA">Run A's aggregate.</param>
    /// <param name="aggregateB">Run B's aggregate.</param>
    /// <param name="costComparable">Whether the pair's costs share one basis.</param>
    /// <param name="costRefusalReason">Why costs are not comparable, when they are not.</param>
    /// <returns>The measured totals payload dictionary.</returns>
    private static Dictionary<string, object?> BuildMeasuredTotals(
        TurnAggregate aggregateA,
        TurnAggregate aggregateB,
        bool costComparable,
        string? costRefusalReason)
    {
        Dictionary<string, object?> totals = new(StringComparer.Ordinal)
        {
            ["availability"] = "measured",
            ["tokensA"] = aggregateA.Tokens,
            ["tokensB"] = aggregateB.Tokens,
            ["tokenDelta"] = aggregateB.Tokens - aggregateA.Tokens,
            ["turnCountA"] = aggregateA.Turns,
            ["turnCountB"] = aggregateB.Turns,
            ["turnDelta"] = aggregateB.Turns - aggregateA.Turns,
            ["costComparable"] = costComparable,
        };

        if (aggregateA.Cost.HasValue)
        {
            totals["costA"] = aggregateA.Cost.Value;
        }

        if (aggregateB.Cost.HasValue)
        {
            totals["costB"] = aggregateB.Cost.Value;
        }

        if (aggregateA.Cost.HasValue && aggregateB.Cost.HasValue)
        {
            totals["costDelta"] = aggregateB.Cost.Value - aggregateA.Cost.Value;
        }

        if (costRefusalReason is not null)
        {
            totals["costRefusalReason"] = costRefusalReason;
        }

        return totals;
    }

    /// <summary>Builds the withheld aggregate totals row: turn counts stay, token sums go with a reason.</summary>
    /// <param name="reason">Why the token totals are not comparable.</param>
    /// <param name="aggregateA">Run A's aggregate.</param>
    /// <param name="aggregateB">Run B's aggregate.</param>
    /// <param name="costComparable">Whether the pair's costs share one basis.</param>
    /// <param name="costRefusalReason">Why costs are not comparable, when they are not.</param>
    /// <returns>The unavailable totals payload dictionary.</returns>
    private static Dictionary<string, object?> BuildUnavailableTotals(
        string reason,
        TurnAggregate aggregateA,
        TurnAggregate aggregateB,
        bool costComparable,
        string? costRefusalReason)
    {
        Dictionary<string, object?> totals = new(StringComparer.Ordinal)
        {
            ["availability"] = "unavailable",
            ["reason"] = reason,
            ["turnCountA"] = aggregateA.Turns,
            ["turnCountB"] = aggregateB.Turns,
            ["turnDelta"] = aggregateB.Turns - aggregateA.Turns,
            ["costComparable"] = costComparable,
        };

        if (costRefusalReason is not null)
        {
            totals["costRefusalReason"] = costRefusalReason;
        }

        return totals;
    }

    /// <summary>
    /// Builds the public-route verdict: pairing is proposed only, because the route never treats a
    /// caller-supplied count as store-backed recommendation history (issue #190).
    /// </summary>
    /// <param name="runAId">Baseline run identifier.</param>
    /// <param name="runBId">Comparison run identifier.</param>
    /// <param name="pairCount">Phase-aligned pairs produced.</param>
    /// <returns>The verdict payload object.</returns>
    private static object BuildVerdict(string runAId, string runBId, int pairCount) => new
    {
        status = "insufficient_history",
        pairCount,
        historyAvailability = "unavailable",
        meetsSufficiencyThreshold = false,
        canPromote = false,
        recommendation =
            "Pairing is manual-only until recommendation history is measured. At least 5 completed pairs without outcome regression are required before promoting automated recommendations.",
        refusalReason =
            "Recommendation history is not measured for this comparison; promotion requires an explicitly recorded completed-pair count of at least 5.",
        summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Compared Run {runAId} against Run {runBId}: {pairCount} phase-aligned pairs across {TaskPhases.Length} phases. Outcome: unobserved -> unobserved."),
    };

    /// <summary>Names why the aggregate token totals were withheld, or answers <see langword="null"/> when they were measured.</summary>
    /// <param name="runAId">Baseline run identifier.</param>
    /// <param name="runBId">Comparison run identifier.</param>
    /// <param name="aggregateA">Run A's aggregate.</param>
    /// <param name="aggregateB">Run B's aggregate.</param>
    /// <returns>The withholding reason, or <see langword="null"/> when the totals are comparable.</returns>
    private static string? TotalsUnavailableReason(
        string runAId,
        string runBId,
        TurnAggregate aggregateA,
        TurnAggregate aggregateB)
    {
        if (aggregateA.Turns == 0 || aggregateB.Turns == 0)
        {
            if (aggregateA.Turns == 0 && aggregateB.Turns == 0)
            {
                return "Neither selected run resolved comparable model turns for phase alignment.";
            }

            return aggregateA.Turns == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Run {runAId} resolved no comparable model turns for phase alignment.")
                : string.Create(CultureInfo.InvariantCulture, $"Run {runBId} resolved no comparable model turns for phase alignment.");
        }

        if (aggregateA.UnavailableReason is not null && aggregateB.UnavailableReason is not null)
        {
            return $"Token telemetry is not measurable for both runs ({aggregateA.UnavailableReason}; {aggregateB.UnavailableReason})";
        }

        if (aggregateA.UnavailableReason is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Token telemetry is not measurable for Run {runAId}: {aggregateA.UnavailableReason}");
        }

        if (aggregateB.UnavailableReason is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Token telemetry is not measurable for Run {runBId}: {aggregateB.UnavailableReason}");
        }

        return null;
    }

    /// <summary>Decides whether the pair's costs may be compared: only through one declared basis.</summary>
    /// <param name="turnsA">Run A's phased turns.</param>
    /// <param name="turnsB">Run B's phased turns.</param>
    /// <returns>Whether the costs are comparable, and why not when they are not.</returns>
    private static (bool CostComparable, string? CostRefusalReason) CostComparability(List<PhaseTurn> turnsA, List<PhaseTurn> turnsB)
    {
        CostBasis? basisA = turnsA.Count > 0 ? turnsA[0].Turn.Cost?.Basis : null;
        CostBasis? basisB = turnsB.Count > 0 ? turnsB[0].Turn.Cost?.Basis : null;
        if (basisA is null || basisB is null)
        {
            return (true, null);
        }

        if (basisA.Value != basisB.Value)
        {
            return (
                false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cost bases differ ({WireBasis(basisA.Value)} vs {WireBasis(basisB.Value)}); refusing to compare costs directly"));
        }

        return (true, null);
    }

    /// <summary>Names a cost basis the way the canonical wire converter does, for refusal messages.</summary>
    /// <param name="basis">The basis being named.</param>
    /// <returns>The wire name of the basis.</returns>
    private static string WireBasis(CostBasis basis) =>
        basis switch
        {
            CostBasis.Published => "published",
            CostBasis.Harness => "harness",
            _ => "unknown",
        };

    /// <summary>Extracts a priced USD cost from a read-time cost summary, keeping unpriced costs out of sums.</summary>
    /// <param name="cost">The turn's published cost summary.</param>
    /// <returns>The USD value when priced and finite, or <see langword="null"/>.</returns>
    private static double? UsdCost(ReadTimeCostSummary? cost) =>
        cost is not null
            && cost.Status == CostStatus.Priced
            && string.Equals(cost.Currency, "USD", StringComparison.Ordinal)
            && cost.Value is double value
            && double.IsFinite(value)
            ? value
            : null;

    /// <summary>Formats a token count the way the retired engine's readings did, with grouping separators.</summary>
    /// <param name="tokens">The measured count.</param>
    /// <returns>The formatted count, or the honest gap phrase when withheld.</returns>
    private static string FormatTokens(long? tokens) =>
        tokens.HasValue
            ? tokens.Value.ToString("N0", CultureInfo.InvariantCulture)
            : "tokens unavailable";

    /// <summary>Renders the tool-evidence suffix a reading appends for one turn.</summary>
    /// <param name="tools">The turn's tool names.</param>
    /// <returns>The suffix, or an empty string when the turn used no tools.</returns>
    private static string ToolsSuffix(IReadOnlyList<string> tools) =>
        tools.Count == 0 ? "" : $" [tools: {string.Join(", ", tools)}]";
}
