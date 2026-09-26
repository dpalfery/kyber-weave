using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// The last gate before a squad mutation: echoes the resolved absolute target root and
/// its scope, then — only when the console is interactive and <c>--yes</c> was not
/// supplied — prompts exactly once for confirmation.
/// </summary>
/// <remarks>
/// <para>
/// The echo always runs, on every path, before any branch: the incident this gate exists
/// for was a write to a silently-guessed directory that nothing named until it happened
/// (plan D2/N4). It also runs on the decline path, so the operator is seen declining a
/// root they were shown, and in non-interactive runs, where the echo in CI logs is the
/// after-the-fact audit trail.
/// </para>
/// <para>
/// A <c>--global</c> run's state anchor is not where the lifecycle writes: every
/// deployed file lands beneath each selected target's own physical global root. The
/// confirmation therefore also names those roots — in the echo and in the prompt —
/// because an approval naming directories other than the ones about to change is not an
/// informed approval.
/// </para>
/// <para>
/// Only an interactive console without <c>--yes</c> is prompted (N1/N2): a scripted
/// caller is deterministic by construction and must never block on stdin, while
/// <c>--yes</c> exists for automation running under a pty that would otherwise hang on
/// the prompt. The <c>readAnswer</c> parameter of <see cref="Confirm"/> is the decision
/// seam (N7): the question in, the decision out, so hosts and tests inject the decision
/// without coupling to answer-parsing conventions.
/// </para>
/// </remarks>
public static class SquadTargetRootConfirmation
{
    /// <summary>
    /// Echoes the resolved absolute <paramref name="root"/> and its scope, prompts exactly
    /// once when the console is interactive and <paramref name="yes"/> is
    /// <c>false</c>, and returns whether the mutation may proceed.
    /// </summary>
    /// <param name="root">The resolved absolute target root, echoed verbatim.</param>
    /// <param name="scope">Whether the run writes to project or global scope.</param>
    /// <param name="verb">The squad command being confirmed, such as <c>install</c>.</param>
    /// <param name="isInteractive">Whether a human is at the console to answer a prompt.</param>
    /// <param name="yes">Whether <c>--yes</c> was passed, which proceeds without prompting.</param>
    /// <param name="readAnswer">Asks the question and returns whether the answer confirms.</param>
    /// <param name="globalTargetRoots">
    /// The physical global root each selected target will be written beneath, for a
    /// <c>--global</c> run; each is echoed and carried into the prompt question. Null or
    /// empty for project scope, where the anchor <paramref name="root"/> is itself the
    /// physical root.
    /// </param>
    public static bool Confirm(
        string root,
        SquadDeploymentScope scope,
        string verb,
        bool isInteractive,
        bool yes,
        Func<string, bool> readAnswer,
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(readAnswer);

        string scopeWord = scope == SquadDeploymentScope.Global ? "global" : "project";

        // The echo is the audit trail and must land on every path — accept, decline,
        // --yes, non-interactive — because it is the only output that names where the
        // write is headed before it happens (N4). The root arrives already resolved to
        // its absolute form and is echoed verbatim: re-resolving could rewrite it.
        //
        // The fold width is lifted for these lines: a redirected console folds at 80
        // columns, splitting a long root across lines, which would corrupt the one thing
        // the lines exist for — a copy-pastable, log-greppable path. The width is
        // restored immediately; rendering is single-threaded here (a CLI run, and the
        // tests serialize their console captures), so nothing renders inside the window.
        SquadCommandComposition.WriteUnfolded(() =>
        {
            AnsiConsole.MarkupLine(
                $"[bold]kyber-weave squad {Markup.Escape(verb)}[/]: target root [bold]{Markup.Escape(root)}[/] ({scopeWord} scope).");
            if (globalTargetRoots is { Count: > 0 })
            {
                foreach ((SquadTarget target, string globalRoot) in globalTargetRoots)
                {
                    AnsiConsole.MarkupLine(
                        $"  [bold]{Markup.Escape(SquadTargetCatalog.GetToken(target))}[/] writes beneath [bold]{Markup.Escape(globalRoot)}[/].");
                }
            }
        });

        // Exactly one prompt, and only for a human at an interactive console who did not
        // pass --yes; every other mode proceeds without touching the answer seam (N1/N2).
        if (!isInteractive || yes) return true;

        string question = globalTargetRoots is { Count: > 0 }
            ? $"Proceed with '{verb}' at '{root}' ({scopeWord} scope), writing beneath: " +
              string.Join(
                  ", ",
                  globalTargetRoots.Select(pair => $"{SquadTargetCatalog.GetToken(pair.Target)} '{pair.GlobalRoot}'")) +
              "? (y/n)"
            : $"Proceed with '{verb}' at '{root}' ({scopeWord} scope)? (y/n)";
        return readAnswer(question);
    }

    /// <summary>
    /// The default answer seam: writes the question and reads one line of console input.
    /// Only an explicit y/yes proceeds — any other answer, and an end-of-input read,
    /// declines, because input that cannot be read cannot be taken as confirming a write.
    /// </summary>
    public static bool ReadConsoleAnswer(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        AnsiConsole.Markup($"[bold]{Markup.Escape(question)}[/] ");
        string? answer = Console.ReadLine();
        return answer is not null &&
               (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
