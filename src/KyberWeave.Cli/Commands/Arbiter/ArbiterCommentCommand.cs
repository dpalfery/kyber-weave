using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Classifies one bot review comment as real, nitpick or already-fixed against the code at the cited line, and records
/// the decision. Without a loadable configuration the comment is classed real. While the comment rule is in shadow the
/// class printed is real, and a line after it says what the model would have given.
/// </summary>
public sealed class ArbiterCommentCommand : Command<ArbiterCommentSettings>
{
    /// <summary>
    /// Builds the provider for a repository root. The command uses the root's configuration; a test substitutes one that
    /// answers, so the printed class does not depend on a live provider.
    /// </summary>
    internal Func<string, DiagnosticReport, IArbiterProvider?> ProviderFactory { get; init; } = ProviderFor;

    /// <summary>Prints the class and the decision id. Exits 1 when the configured provider cannot be built.</summary>
    public override int Execute(CommandContext context, ArbiterCommentSettings settings, CancellationToken cancellationToken)
    {
        // The same size limit the MCP tool applies, checked before any read, provider call or ledger append.
        if (settings.Body.Length > ArbiterTextLimits.MaxTextChars)
        {
            AnsiConsole.MarkupLine($"[red]The comment is larger than {ArbiterTextLimits.MaxTextChars} characters.[/]");
            return 1;
        }

        string root = System.IO.Path.GetFullPath(settings.Root);
        // The same containment the served tool applies: a cited path that leaves the repository root is refused, not read.
        if (!settings.Summary && FileTextFacts.ResolveWithinRoot(ArbiterRepoRoot.Find(root), settings.Path) is null)
        {
            AnsiConsole.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' was not found under the repository root.[/]");
            return 1;
        }

        DiagnosticReport report = new();
        IArbiterProvider? provider = ProviderFactory(root, report);
        if (provider is null)
        {
            foreach (Diagnostic diagnostic in report.Items)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(diagnostic.Message)}[/]");
            }

            return 1;
        }

        using IDisposable? owner = provider as IDisposable;
        if (settings.Summary)
        {
            CommentSummaryResult summary = new CommentTriageService(
                    provider,
                    root,
                    System.IO.Path.Combine(root, "artifacts", "arbiter"),
                    TimeProvider.System,
                    null,
                    BotLoginsFor(root),
                    RulesFor(root))
                .ClassifySummaryAsync(new CommentSummaryRequest(settings.Body, settings.Author), cancellationToken)
                .GetAwaiter()
                .GetResult();
            foreach (CommentSummaryItem item in summary.Items)
            {
                string shadowNote = item.Shadow
                    ? $" ({Markup.Escape(ShadowNote(item.WouldClass))})"
                    : NotAnsweredNote(item.Status);
                AnsiConsole.MarkupLine(
                    $"{Markup.Escape(item.Path)}:{item.Line} [bold]{Markup.Escape(item.Class)}[/] {Markup.Escape(item.Issue)}{shadowNote}");
            }

            return 0;
        }

        CommentTriageResult result = new CommentTriageService(
                provider,
                root,
                System.IO.Path.Combine(root, "artifacts", "arbiter"),
                TimeProvider.System,
                rules: RulesFor(root))
            .ClassifyAsync(new CommentTriageRequest(settings.Path, settings.Line, settings.Body, settings.CommitSha), cancellationToken)
            .GetAwaiter()
            .GetResult();
        AnsiConsole.MarkupLine($"class [bold]{Markup.Escape(result.Class)}[/]");
        if (result.Shadow)
        {
            AnsiConsole.MarkupLine(Markup.Escape(ShadowNote(result.WouldClass)));
        }
        else if (result.Status != ArbiterProviderStatuses.Answered)
        {
            AnsiConsole.MarkupLine($"status {Markup.Escape(result.Status)}");
        }

        AnsiConsole.MarkupLine($"decision {Markup.Escape(result.DecisionId)}");
        return 0;
    }

    /// <summary>
    /// The line that says what a shadow rule would have given: the class, or that nothing was answered. Printed beside
    /// the real class so an operator can see the rule is log-only and what it would have said.
    /// </summary>
    private static string ShadowNote(string? wouldClass) =>
        wouldClass is null ? "shadow: nothing answered" : $"shadow: would have been {wouldClass}";

    /// <summary>The note a summary row prints when the rule is not in shadow and the row was not answered; empty when it was.</summary>
    private static string NotAnsweredNote(string status) =>
        status == ArbiterProviderStatuses.Answered ? string.Empty : $" ({Markup.Escape(status)})";

    /// <summary>
    /// The effective rules the root's configuration holds, with host overrides applied. A root with no loadable
    /// configuration uses the shipped rules, so the comment rule's shipped shadow state applies.
    /// </summary>
    private static IReadOnlyList<ArbiterRule> RulesFor(string root)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        return loaded.Success && loaded.Config is not null ? loaded.Config.Arbiter.Rules : ArbiterConfig.ProductDefaults.Rules;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterCommentSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// The provider the root's configuration names. A root with no loadable configuration answers with
    /// <see cref="NoneProvider"/>; a configuration whose provider override cannot be built returns null and reports why.
    /// </summary>
    private static IArbiterProvider? ProviderFor(string root, DiagnosticReport report)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        if (!loaded.Success || loaded.Config is null)
        {
            return new NoneProvider();
        }

        return ArbiterCommandComposition.CreateProvider(loaded.Config, null, report, out _) ?? (report.HasErrors ? null : new NoneProvider());
    }

    /// <summary>
    /// The bot logins the root's <c>arbiter.comment-triage.bot-logins</c> names. A root with no loadable configuration
    /// has none, so every summary stays real.
    /// </summary>
    private static IReadOnlyList<string> BotLoginsFor(string root)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        return loaded.Success && loaded.Config is not null ? loaded.Config.Arbiter.CommentTriage.BotLogins : [];
    }
}
