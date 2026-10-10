using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Reports what the decision log and ledger say about past dispatches.</summary>
public sealed class ArbiterAuditCommand : Command<ArbiterSettings>
{
    /// <summary>A ledger event with no decision: the hook was killed, timed out or crashed after recording the event.</summary>
    public const string MissingDecision = "KW-ARB-AUDIT-001";

    /// <summary>An unmarked dispatch, with an unidentified caller, whose target is a Squad agent.</summary>
    public const string UnmarkedSquadDispatch = "KW-ARB-AUDIT-002";

    /// <summary>An unpaired event: a pre with no post although its session has later events, or a post with no pre.</summary>
    public const string UnpairedEvent = "KW-ARB-AUDIT-003";

    /// <summary>A dispatch to an implementation specialist whose packet body names a planning path.</summary>
    public const string PlanningPathInBody = "KW-ARB-AUDIT-004";

    /// <summary>An attestation listed for information; never a finding.</summary>
    public const string AttestationListed = "KW-ARB-AUDIT-005";

    /// <summary>
    /// Reads the ledger and the decision log under <c>artifacts/arbiter/</c> and
    /// reports <c>KW-ARB-AUDIT-001</c> to <c>-004</c> as warnings, filtered by
    /// <c>--plan</c>, <c>--session</c> and <c>--since</c>. Attestations are
    /// listed for information and are not findings. A dispatch no hook
    /// observed leaves no record, so <c>audit</c> cannot report it. Exits 1 on
    /// an unreadable filter or configuration; findings alone do not fail the run.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(report, settings, "arbiter audit", "Event");
            return 1;
        }

        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (!DateTimeOffset.TryParse(
                    settings.Since,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    $"--since '{settings.Since}' is not an ISO-8601 timestamp.",
                    "--since",
                    Hint: "Pass an ISO-8601 timestamp, for example '2026-10-01T00:00:00Z'."));
                CommandHelpers.Finish(report, settings, "arbiter audit", "Event");
                return 1;
            }

            since = parsed;
        }

        _ = config;
        string arbiterDirectory = ArbiterCommandComposition.ResolveArbiterDirectory(settings.Path);
        InFlightLedger ledger = new(arbiterDirectory);
        DecisionLog decisions = new(arbiterDirectory);

        IReadOnlyList<ArbiterLedgerEvent> events;
        IReadOnlyList<ArbiterDecisionRecord> records;
        try
        {
            events = ledger.ReadAll();
            records = decisions.ReadAll();
        }
        // A truncated, hand-edited or foreign line is malformed JSON, which throws
        // JsonException rather than an IOException. Reading the log must fail closed the
        // same way either way, or the audit crashes on the corrupt file it exists to
        // report on.
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or NotSupportedException
            or System.IO.InvalidDataException)
        {
            report.Add(new Diagnostic(
                ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                $"The arbiter log under '{arbiterDirectory}' could not be read: {exception.Message}",
                arbiterDirectory,
                "The ledger or the decision log holds a line that is not a complete record; the audit fails closed rather than reporting a partial log."));
            CommandHelpers.Finish(report, settings, "arbiter audit", "Event");
            return 1;
        }

        HashSet<string> decided = new(
            records.Select(record => record.LedgerId),
            StringComparer.Ordinal);
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        int reported = 0;
        foreach (ArbiterLedgerEvent ev in events
                     .Where(e => InScope(e, settings, since))
                     .OrderBy(e => e.At))
        {
            CheckMissingDecision(ev, decided, report, ref reported);
            CheckUnmarkedSquadDispatch(ev, catalog, report, ref reported);
            CheckUnpaired(ev, events, ledger, report, ref reported);
            CheckPlanningPath(ev, report, ref reported);
        }

        List<string> attestations = [.. events
            .Where(e => InScope(e, settings, since) && e.Returns?.Attested is { Count: > 0 })
            .SelectMany(e => (e.Returns!.Attested ?? []).Select(a => (Event: e, Attested: a)))
            .Select(pair => $"event {pair.Event.Id} attested {pair.Attested}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        foreach (string attestation in attestations)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(attestation)}.[/]");
            report.Add(new Diagnostic(
                AttestationListed,
                Severity.Info,
                attestation + ".",
                "attested"));
        }

        AnsiConsole.MarkupLine($"[grey]{reported} finding(s) over {events.Count} ledger event(s).[/]");

        CommandHelpers.Finish(report, settings, "arbiter audit", "Event");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static bool InScope(ArbiterLedgerEvent ev, ArbiterSettings settings, DateTimeOffset? since)
    {
        if (!string.IsNullOrWhiteSpace(settings.Session)
            && !string.Equals(ev.Session, settings.Session, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.Plan)
            && !string.Equals(ev.Headers?.PlanFile, settings.Plan, StringComparison.Ordinal))
        {
            return false;
        }

        if (since is not null && ev.At < since.Value)
        {
            return false;
        }

        return true;
    }

    private static void CheckMissingDecision(
        ArbiterLedgerEvent ev,
        HashSet<string> decided,
        DiagnosticReport report,
        ref int reported)
    {
        if (decided.Contains(ev.Id))
            return;

        reported++;
        report.Add(new Diagnostic(
            MissingDecision,
            Severity.Warning,
            $"Ledger event '{ev.Id}' has no decision: the hook was killed, timed out or crashed after recording it.",
            ev.Id,
            ev.Session,
            "Re-run the dispatch with the hook installed, or confirm the dispatch never ran."));
    }

    private static void CheckUnmarkedSquadDispatch(
        ArbiterLedgerEvent ev,
        ArbiterSquadCatalog catalog,
        DiagnosticReport report,
        ref int reported)
    {
        if (!string.Equals(ev.Phase, ArbiterLedgerPhases.Unmarked, StringComparison.Ordinal))
            return;
        if (!string.IsNullOrWhiteSpace(ev.Caller))
            return;
        if (string.IsNullOrWhiteSpace(ev.Target) || !catalog.Agents.ContainsKey(ev.Target))
            return;

        reported++;
        report.Add(new Diagnostic(
            UnmarkedSquadDispatch,
            Severity.Warning,
            $"Unmarked dispatch to Squad agent '{ev.Target}' with an unidentified caller.",
            ev.Id,
            ev.Session,
            "Confirm the caller: an unmarked Squad dispatch bypassed the pre-dispatch gate."));
    }

    private static void CheckUnpaired(
        ArbiterLedgerEvent ev,
        IReadOnlyList<ArbiterLedgerEvent> events,
        InFlightLedger ledger,
        DiagnosticReport report,
        ref int reported)
    {
        if (string.Equals(ev.Phase, ArbiterLedgerPhases.Pre, StringComparison.Ordinal))
        {
            bool hasPost = events.Any(candidate =>
                string.Equals(candidate.Phase, ArbiterLedgerPhases.Post, StringComparison.Ordinal) &&
                string.Equals(candidate.PreId, ev.Id, StringComparison.Ordinal));
            if (hasPost)
                return;

            bool sessionMovedOn = ev.Session is not null && events.Any(candidate =>
                string.Equals(candidate.Session, ev.Session, StringComparison.Ordinal) &&
                candidate.At > ev.At);
            if (!sessionMovedOn)
                return;

            reported++;
            report.Add(new Diagnostic(
                UnpairedEvent,
                Severity.Warning,
                $"Pre event '{ev.Id}' has no post although its session has later events: post-dispatch rules did not run.",
                ev.Id,
                ev.Session));
            return;
        }

        if (string.Equals(ev.Phase, ArbiterLedgerPhases.Post, StringComparison.Ordinal)
            && ev.PreId is null
            && ledger.MatchPre(ev) is null)
        {
            reported++;
            report.Add(new Diagnostic(
                UnpairedEvent,
                Severity.Warning,
                $"Post event '{ev.Id}' has no pre: the pre-dispatch gate did not run.",
                ev.Id,
                ev.Session));
        }
    }

    private static void CheckPlanningPath(
        ArbiterLedgerEvent ev,
        DiagnosticReport report,
        ref int reported)
    {
        if (ev.BodyNamesPlanningPath is not true)
            return;

        reported++;
        report.Add(new Diagnostic(
            PlanningPathInBody,
            Severity.Warning,
            $"Dispatch '{ev.Id}' names a planning path in an implementation packet.",
            ev.Id,
            ev.Session,
            "Route planning work through the planner instead of the implementation specialist."));
    }
}
