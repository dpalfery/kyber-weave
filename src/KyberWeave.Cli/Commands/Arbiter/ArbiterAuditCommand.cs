using System.Globalization;
using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Reports what the decision log and ledger say about past dispatches.</summary>
public sealed class ArbiterAuditCommand(
    IArbiterConfigResolver configs,
    IArbiterRepositoryLocator locator,
    IArbiterAuditService audits,
    IRepositoryFileStore files,
    IAnsiConsole console,
    PlanArguments plans) : Command<ArbiterSettings>
{
    /// <summary>A ledger event with no decision: the hook was killed, timed out or crashed after recording the event.</summary>
    public const string MissingDecision = ArbiterAuditService.MissingDecision;

    /// <summary>An unmarked dispatch, with an unidentified caller, whose target is a Squad agent.</summary>
    public const string UnmarkedSquadDispatch = ArbiterAuditService.UnmarkedSquadDispatch;

    /// <summary>An unpaired event: a pre with no post although its session has later events, or a post with no pre.</summary>
    public const string UnpairedEvent = ArbiterAuditService.UnpairedEvent;

    /// <summary>A dispatch to an implementation specialist whose packet body names a planning path.</summary>
    public const string PlanningPathInBody = ArbiterAuditService.PlanningPathInBody;

    /// <summary>An attestation listed for information; never a finding.</summary>
    public const string AttestationListed = ArbiterAuditService.AttestationListed;

    /// <summary>
    /// The decision model is configured and dispatches went to implementation agents, yet no rule asked it anything:
    /// the judgement rules did not run (ADR 0082). Silent when no model is configured.
    /// </summary>
    public const string NoModelEvaluations = ArbiterAuditService.NoModelEvaluations;

    /// <summary>
    /// The ledger or the decision log is longer than the window one read looks at, so its older records were not
    /// read and what follows describes only the newest part. Also reported by <c>arbiter tune</c>.
    /// </summary>
    public const string LogWindowTruncated = ArbiterAuditService.LogWindowTruncated;

    /// <summary>
    /// Reads the ledger and the decision log under <c>artifacts/arbiter/</c> and
    /// reports <c>KW-ARB-AUDIT-001</c> to <c>-004</c> as warnings, filtered by
    /// <c>--plan</c>, <c>--session</c> and <c>--since</c>. Attestations are
    /// listed for information and are not findings. A dispatch no hook
    /// observed leaves no record, so <c>audit</c> cannot report it. Exits 1 on
    /// an unreadable filter or configuration; findings alone do not fail the run.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!configs.TryResolve(settings.Path, settings.Config, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(console, report, settings, "arbiter audit", "Event");
            return 1;
        }

        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (!DateTimeOffset.TryParse(
                    settings.Since,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    $"--since '{settings.Since}' is not an ISO-8601 timestamp.",
                    "--since",
                    Hint: "Pass an ISO-8601 timestamp, for example '2026-10-01T00:00:00Z'."));
                CommandHelpers.Finish(console, report, settings, "arbiter audit", "Event");
                return 1;
            }

            since = parsed;
        }

        string repositoryRoot = locator.ResolveRepositoryRoot(settings.Path);
        Func<string?, bool>? matchesPlan = null;
        if (!string.IsNullOrWhiteSpace(settings.Plan))
        {
            // --plan takes the spellings the plan commands accept, so an agent that types a slug or a bare file name
            // still matches the repository-relative path the dispatch header carried.
            if (!plans.TryResolve(repositoryRoot, config, files, settings.Plan, settings.WorkingDirectory, "arbiter audit", report, out string relative))
            {
                CommandHelpers.Finish(console, report, settings, "arbiter audit", "Event");
                return 1;
            }

            matchesPlan = new PlanFilter(repositoryRoot, plans.ResolverFor(repositoryRoot, config, files), relative).Matches;
        }

        if (audits.Audit(repositoryRoot, config, since, settings.Session, matchesPlan, report) is not { } audited)
        {
            CommandHelpers.Finish(console, report, settings, "arbiter audit", "Event");
            return 1;
        }

        foreach (string attestation in audited.Attestations)
        {
            console.MarkupLine($"[grey]{Markup.Escape(attestation)}.[/]");
        }

        console.MarkupLine(
            $"[grey]{audited.ModelEvaluations} decision-model evaluation(s) over {audited.ImplementationDispatches} dispatch(es) to implementation agents.[/]");
        console.MarkupLine($"[grey]{audited.Findings} finding(s) over {audited.EventCount} ledger event(s).[/]");

        CommandHelpers.Finish(console, report, settings, "arbiter audit", "Event");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>The plan <c>--plan</c> named, resolved to its repository-relative path, and the same resolution of a ledger event's plan file.</summary>
    private sealed class PlanFilter(string root, PlanFileResolver resolver, string relative)
    {
        private readonly PlanFileResolver _resolver = resolver;
        private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);

        public bool Matches(string? eventPlan)
        {
            if (string.IsNullOrWhiteSpace(eventPlan))
                return false;

            if (!_seen.TryGetValue(eventPlan, out string? canonical))
            {
                PlanPathResolution resolution = _resolver.Resolve(eventPlan, root);
                canonical = resolution.Found
                    ? resolution.Path!
                    : Path.GetRelativePath(root, Path.GetFullPath(eventPlan, root)).Replace('\\', '/');
                _seen[eventPlan] = canonical;
            }

            return string.Equals(canonical, relative, StringComparison.Ordinal);
        }
    }
}
