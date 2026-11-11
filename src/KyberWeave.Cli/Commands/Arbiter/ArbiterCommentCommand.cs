using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Classifies one bot review comment as real, nitpick or already-fixed against the code at the cited line, and records
/// the decision. Without a loadable configuration the comment is classed real.
/// </summary>
public sealed class ArbiterCommentCommand : Command<ArbiterCommentSettings>
{
    /// <summary>Prints the class and the decision id. Exits 1 when the configured provider cannot be built.</summary>
    public override int Execute(CommandContext context, ArbiterCommentSettings settings, CancellationToken cancellationToken)
    {
        string root = System.IO.Path.GetFullPath(settings.Root);
        DiagnosticReport report = new();
        IArbiterProvider? provider = ProviderFor(root, report);
        if (provider is null)
        {
            foreach (Diagnostic diagnostic in report.Items)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(diagnostic.Message)}[/]");
            }

            return 1;
        }

        using IDisposable? owner = provider as IDisposable;
        CommentTriageResult result = new CommentTriageService(
                provider,
                root,
                System.IO.Path.Combine(root, "artifacts", "arbiter"),
                TimeProvider.System)
            .ClassifyAsync(new CommentTriageRequest(settings.Path, settings.Line, settings.Body, settings.CommitSha), cancellationToken)
            .GetAwaiter()
            .GetResult();
        AnsiConsole.MarkupLine($"class [bold]{Markup.Escape(result.Class)}[/]");
        AnsiConsole.MarkupLine($"decision {Markup.Escape(result.DecisionId)}");
        return 0;
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
}
