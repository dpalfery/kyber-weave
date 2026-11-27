using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Arbiter;

/// <summary>Reads the Arbiter ledger and decision log and reports what they say about past dispatches.</summary>
/// <param name="locator">Finds the repository's <c>artifacts/arbiter/</c> directory.</param>
/// <param name="state">Opens the ledger and the decision log.</param>
/// <param name="environment">Supplies the home directory the user settings live under.</param>
/// <param name="userSettings">Applies the user override that decides whether a model is configured.</param>
public sealed class ArbiterAuditService(
    IArbiterRepositoryLocator locator,
    IArbiterStateFactory state,
    IProcessEnvironment environment,
    IArbiterUserSettings userSettings) : IArbiterAuditService
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
    /// The decision model is configured and dispatches went to implementation agents, yet no rule asked it anything:
    /// the judgement rules did not run (ADR 0082). Silent when no model is configured.
    /// </summary>
    public const string NoModelEvaluations = "KW-ARB-AUDIT-006";

    /// <summary>
    /// The ledger or the decision log is longer than the window one read looks at, so its older records were not
    /// read and what follows describes only the newest part. Also reported by <c>arbiter tune</c>.
    /// </summary>
    public const string LogWindowTruncated = "KW-ARB-AUDIT-007";

    /// <inheritdoc />
    public ArbiterAuditReport? Audit(
        string repositoryPath,
        KyberWeaveConfig config,
        DateTimeOffset? since,
        string? session,
        Func<string?, bool>? matchesPlan,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);

        string arbiterDirectory = locator.ResolveArbiterDirectory(repositoryPath);
        IArbiterLedger ledger = state.OpenLedger(arbiterDirectory);
        IArbiterDecisionLog decisions = state.OpenDecisionLog(arbiterDirectory);

        IReadOnlyList<ArbiterLedgerEvent> events;
        IReadOnlyList<ArbiterDecisionRecord> records;
        try
        {
            ArbiterLogRead<ArbiterLedgerEvent> ledgerRead = ledger.ReadAllWithDiagnostics();
            ArbiterLogRead<ArbiterDecisionRecord> decisionRead = decisions.ReadAllWithDiagnostics();
            events = ledgerRead.Records;
            records = decisionRead.Records;
            if (ArbiterLogProblems.Unreadable(ledgerRead, decisionRead) is { } unreadable)
            {
                report.Add(new Diagnostic(
                    ArbiterEvaluator.HookErrorCode,
                    Severity.Error,
                    $"The arbiter log under '{arbiterDirectory}' exists but could not be read: {unreadable}",
                    arbiterDirectory,
                    "The audit fails closed rather than reporting an unreadable log as an empty one."));
                return null;
            }

            if (ledgerRead.MalformedLines + decisionRead.MalformedLines > 0)
            {
                report.Add(new Diagnostic(
                    ArbiterEvaluator.HookErrorCode,
                    Severity.Error,
                    $"The arbiter log under '{arbiterDirectory}' holds " +
                    $"{ledgerRead.MalformedLines + decisionRead.MalformedLines} line(s) that are not a complete record.",
                    arbiterDirectory,
                    "The ledger or the decision log holds a line that is not a complete record; the audit fails closed rather than reporting a partial log."));
                return null;
            }

            if (ArbiterLogProblems.TruncationNote(ledgerRead, decisionRead) is { } truncated)
            {
                report.Add(new Diagnostic(
                    LogWindowTruncated,
                    Severity.Warning,
                    $"The arbiter log under '{arbiterDirectory}': {truncated}",
                    arbiterDirectory,
                    "Findings below describe only the newest records; older dispatches and decisions are not counted."));
            }
        }
        // A truncated, hand-edited or foreign line is malformed JSON, which throws
        // JsonException rather than an IOException. Reading the log must fail closed the
        // same way either way, or the audit crashes on the corrupt file it exists to
        // report on.
        catch (Exception exception) when (FileFailure.Is(exception) || exception is System.Text.Json.JsonException or InvalidDataException)
        {
            report.Add(new Diagnostic(
                ArbiterEvaluator.HookErrorCode,
                Severity.Error,
                $"The arbiter log under '{arbiterDirectory}' could not be read: {exception.Message}",
                arbiterDirectory,
                "The ledger or the decision log holds a line that is not a complete record; the audit fails closed rather than reporting a partial log."));
            return null;
        }

        HashSet<string> decided = new(
            records.Select(record => record.LedgerId),
            StringComparer.Ordinal);
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        int reported = 0;
        foreach (ArbiterLedgerEvent ev in events
                     .Where(e => InScope(e, since, session, matchesPlan))
                     .OrderBy(e => e.At))
        {
            CheckMissingDecision(ev, decided, report, ref reported);
            CheckUnmarkedSquadDispatch(ev, catalog, report, ref reported);
            CheckUnpaired(ev, events, ledger, report, ref reported);
            CheckPlanningPath(ev, report, ref reported);
        }

        List<string> attestations = [.. events
            .Where(e => InScope(e, since, session, matchesPlan) && e.Returns?.Attested is { Count: > 0 })
            .SelectMany(e => (e.Returns!.Attested ?? []).Select(a => (Event: e, Attested: a)))
            .Select(pair => $"event {pair.Event.Id} attested {pair.Attested}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        foreach (string attestation in attestations)
        {
            report.Add(new Diagnostic(
                AttestationListed,
                Severity.Info,
                attestation + ".",
                "attested"));
        }

        (int evaluations, int dispatches) = CheckModelEvaluations(
            config, events, records, since, session, matchesPlan, report);

        return new ArbiterAuditReport(reported, events.Count, attestations, evaluations, dispatches);
    }

    /// <summary>
    /// Counts the decision-model (step 1) evaluations behind the events in scope and reports
    /// <see cref="NoModelEvaluations"/> when a model is configured, implementation agents were dispatched, and the model
    /// was asked nothing. The count is always reported as a metric; with provider <c>none</c> the finding stays silent.
    /// </summary>
    /// <returns>The evaluations and the implementation dispatches counted.</returns>
    private (int Evaluations, int Dispatches) CheckModelEvaluations(
        KyberWeaveConfig config,
        IReadOnlyList<ArbiterLedgerEvent> events,
        IReadOnlyList<ArbiterDecisionRecord> records,
        DateTimeOffset? since,
        string? session,
        Func<string?, bool>? matchesPlan,
        DiagnosticReport report)
    {
        int reported = 0;
        HashSet<string> scoped = [.. events.Where(e => InScope(e, since, session, matchesPlan)).Select(e => e.Id)];
        List<ArbiterDecisionRecord> inScope = [.. records.Where(record => scoped.Contains(record.LedgerId))];
        // Model targeting rides every writer dispatch whether or not a plan task resolved, so its answers would mask
        // a run whose judgement rules never ran: only the judgement rules on the other triggers count.
        int evaluations = inScope
            .Where(record => !string.Equals(record.Trigger, ArbiterTriggers.DelegateModel, StringComparison.Ordinal))
            .Sum(record => record.Rules.Count(rule =>
            rule.Step == 1 && !string.Equals(rule.Answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)));
        int implementationDispatches = inScope
            .Where(record => !record.Trigger.EndsWith(".returned", StringComparison.Ordinal)
                             && string.Equals(TriggerClassifier.TargetClassFor(record.Target), "implementation", StringComparison.Ordinal))
            .Select(record => record.LedgerId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        report.AddMetric("model-evaluations", evaluations);
        report.AddMetric("implementation-dispatches", implementationDispatches);

        if (evaluations > 0 || implementationDispatches == 0 || !ModelConfigured(config, report, ref reported))
        {
            return (evaluations, implementationDispatches);
        }

        report.Add(new Diagnostic(
            NoModelEvaluations,
            Severity.Warning,
            $"A decision model is configured and {implementationDispatches} dispatch(es) went to implementation agents, " +
            "but no rule asked the model anything: the Arbiter's judgement rules did not run.",
            "arbiter",
            Hint: "Check how those dispatches were classified in the decision log: a writer dispatch classified as " +
                  "investigate runs only the machine-fact rules. See ADR 0082."));
        return (evaluations, implementationDispatches);
    }

    internal bool ModelConfigured(KyberWeaveConfig config, DiagnosticReport report, ref int reported)
    {
        string home = environment.HomeDirectory;
        try
        {
            return userSettings.ApplyTo(config.Arbiter.Provider, home).Kind
                   != ArbiterProviderKind.None;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is YamlDotNet.Core.YamlException)
        {
            reported++;
            report.Add(new Diagnostic(
                NoModelEvaluations,
                Severity.Warning,
                $"The user settings file '{ArbiterUserSettings.GetPath(home)}' could not be read " +
                $"({exception.GetType().Name}: {exception.Message}), so its provider override was ignored and the " +
                "repository's provider decided whether a decision model is configured.",
                "arbiter",
                Hint: "Fix or remove the user settings file, then run the audit again."));
            return config.Arbiter.Provider.Kind != ArbiterProviderKind.None;
        }
    }

    private static bool InScope(
        ArbiterLedgerEvent ev,
        DateTimeOffset? since,
        string? session,
        Func<string?, bool>? matchesPlan)
    {
        if (!string.IsNullOrWhiteSpace(session)
            && !string.Equals(ev.Session, session, StringComparison.Ordinal))
        {
            return false;
        }

        if (matchesPlan is not null && !matchesPlan(ev.Headers?.PlanFile))
            return false;

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
        // An event that classified as no trigger is the pass-through shape: no rule was
        // bound to it, the evaluator returned allow without evaluating, and it records
        // no decision by design. Only a triggered event owes the log a decision, so the
        // exemption keys on the trigger the classifier recorded rather than on the
        // absence of one - a triggered event that died mid-evaluation still reports.
        if (string.IsNullOrWhiteSpace(ev.Trigger))
            return;

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
        // A blank target is flagged as well: a dispatch whose target the harness did not
        // send (Devin with no profile argument) may still be a Squad dispatch.
        bool blankTarget = string.IsNullOrWhiteSpace(ev.Target);
        if (!blankTarget && !catalog.Agents.ContainsKey(ev.Target!))
            return;

        reported++;
        report.Add(new Diagnostic(
            UnmarkedSquadDispatch,
            Severity.Warning,
            blankTarget
                ? "Unmarked dispatch with no target and an unidentified caller."
                : $"Unmarked dispatch to Squad agent '{ev.Target}' with an unidentified caller.",
            ev.Id,
            ev.Session,
            "Confirm the caller: an unmarked Squad dispatch bypassed the pre-dispatch gate."));
    }

    private static void CheckUnpaired(
        ArbiterLedgerEvent ev,
        IReadOnlyList<ArbiterLedgerEvent> events,
        IArbiterLedger ledger,
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
