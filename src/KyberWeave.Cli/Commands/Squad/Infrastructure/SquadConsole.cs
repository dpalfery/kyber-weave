using KyberWeave.Contracts.Host;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// Everything the Squad commands print or ask on the console: markup lines that are never folded, the exit-2 client
/// error, migration and transaction notes, and the one-line confirmation prompt.
/// </summary>
/// <param name="ansi">Renders markup.</param>
/// <param name="standardStreams">Reads input, and reports whether it is redirected.</param>
public sealed class SquadConsole(IAnsiConsole ansi, IConsole standardStreams)
{
    /// <summary>Gets the console markup is rendered on.</summary>
    public IAnsiConsole Ansi { get; } = ansi ?? throw new ArgumentNullException(nameof(ansi));

    /// <summary>Gets the writer for output that is not markup.</summary>
    public TextWriter Out => standardStreams.Out;

    /// <summary>Gets a value indicating whether a person at the console can answer a prompt.</summary>
    public bool IsInteractive => !standardStreams.IsInputRedirected && Ansi.Profile.Capabilities.Interactive;

    /// <summary>Writes one line of markup without folding, so a path in it stays copy-pastable.</summary>
    /// <param name="markup">The markup, already escaped.</param>
    public void WriteLine(string markup) => CommandHelpers.WriteUnfolded(Ansi, () => Ansi.MarkupLine(markup));

    /// <summary>
    /// Writes the exit-2 client-input error line without folding: the hint names the operator's paths verbatim, and a
    /// redirected console folds at 80 columns, splitting a long path across lines.
    /// </summary>
    /// <param name="message">The unescaped message.</param>
    public void WriteClientInputError(string message) =>
        WriteLine($"[red]kyber-weave squad: error: {Markup.Escape(message)}[/]");

    /// <summary>
    /// Surfaces the warnings a committed transaction raised after its commit point. The deployment succeeded, so the
    /// command still exits 0, but the operator must learn that the registry is behind.
    /// </summary>
    /// <param name="warnings">The warnings, each already naming the command that rebuilds the registry.</param>
    public void WriteTransactionWarnings(IReadOnlyList<string>? warnings)
    {
        foreach (string warning in warnings ?? []) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");
    }

    /// <summary>
    ///     Reports a failure a Squad command caught: the recovery notices that travel beside a failure of a type the
    ///     lifecycle must not replace, then the failure's message as the command's error. Every failure goes through
    ///     here, so none escapes as an unhandled exception.
    /// </summary>
    /// <param name="failure">The failure.</param>
    public void WriteFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        WriteTransactionWarnings(SquadLifecycleService.RecoveryNotices(failure));
        WriteClientInputError(failure.Message);
    }

    /// <summary>Writes the notes a state migration produced.</summary>
    /// <param name="report">What the migration did.</param>
    /// <param name="includeRetainedLegacyRoots">Whether to name the legacy roots the migration left behind.</param>
    public void WriteMigrationNotes(SquadMigrationReport report, bool includeRetainedLegacyRoots = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string item in report.MigratedItems) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string item in report.ImportedProjects) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string warning in report.Warnings) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");

        if (!includeRetainedLegacyRoots) return;

        foreach (SquadUnmigratedLegacyRoot root in report.UnmigratedLegacyRoots)
            WriteLine(
                $"[yellow]warning:[/] legacy global root '{Markup.Escape(root.Root)}' was not migrated: " +
                $"{Markup.Escape(root.Reason)} {Markup.Escape(root.Guidance)}");
    }

    /// <summary>Writes the one message the preset verbs share when the model profiles cannot be resolved.</summary>
    public void WriteCanonicalProfilesUnresolvedError() =>
        WriteClientInputError(
            "Could not resolve Squad model profiles from canonical source. " +
            "Run `kyber-weave squad update` to refresh the profile snapshot; " +
            "running from a kyber-weave checkout also works.");

    /// <summary>
    /// Writes the question and reads one line. Only an explicit y or yes proceeds; any other answer, and an end of input,
    /// declines, because input that cannot be read cannot be taken as confirming a write.
    /// </summary>
    /// <param name="question">The question, unescaped.</param>
    /// <returns>Whether the answer confirms.</returns>
    public bool ReadAnswer(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        Ansi.Markup($"[bold]{Markup.Escape(question)}[/] ");
        string? answer = standardStreams.ReadLine();
        return answer is not null &&
               (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs <paramref name="write"/> with folding lifted.</summary>
    /// <param name="write">The output to produce at full width.</param>
    public void WriteUnfolded(Action write) => CommandHelpers.WriteUnfolded(Ansi, write);
}
