using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>
/// Brings a developer task's commits from its own worktree onto the run's branch, keeps the task's scratchpad,
/// and removes the task worktree. The last step of a developer task, so the conductor never has to dispatch a
/// worker just to cherry-pick a commit.
/// </summary>
/// <remarks>
/// Exit codes are the contract the worker branches on: <c>0</c> the run branch holds the task's work (or already
/// did), <c>1</c> a lock or git failure, <c>2</c> refused input or an unusable worktree, <c>3</c> a conflict the
/// conductor reconciles with a rework, and <c>4</c> the commits are integrated but the task's scratchpad could not
/// be copied and the task worktree was left for the caller to remove. The final line is
/// <c>INTEGRATED: &lt;sha&gt;</c>, which the worker copies into its completion digest as <c>COMMIT</c>.
/// </remarks>
public sealed class RunIntegrateCommand(IRunIntegrateService integrate, IProcessEnvironment environment, IAnsiConsole console)
    : Command<RunIntegrateSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description =
        "Fast-forward or cherry-pick a task worktree's commits onto the run branch, keep its scratchpad, and remove " +
        "the task worktree.";

    private const string CommandName = "kyber-weave run integrate";

    /// <summary>The task's work is on the run branch.</summary>
    private const int Integrated = 0;

    /// <summary>The lock could not be taken, or git failed in a way that is not a conflict.</summary>
    private const int Failed = 1;

    /// <summary>The caller supplied input, or a worktree is in a state this step cannot act on.</summary>
    private const int RefusedInput = 2;

    /// <summary>A commit could not be applied; the run branch is unchanged and the task worktree survives.</summary>
    private const int Conflict = 3;

    /// <summary>The commits are integrated, but the scratchpad could not be copied and the task worktree survives.</summary>
    private const int EvidenceFailed = 4;

    /// <inheritdoc />
    public override int Execute(CommandContext context, RunIntegrateSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        RunIntegrateOutcome outcome = integrate.Integrate(
            new RunIntegrateRequest(
                settings.Task, settings.Path, settings.RunWorktree, environment.CurrentDirectory, environment.HomeDirectory));

        foreach (string note in outcome.Notes ?? [])
        {
            console.MarkupLine($"[grey]  {Markup.Escape(note)}[/]");
        }

        switch (outcome.Status)
        {
            case RunIntegrateStatus.Integrated:
                console.WriteLine($"INTEGRATED: {outcome.RunHead ?? "unknown"}");
                return Integrated;

            case RunIntegrateStatus.NothingToIntegrate:
                console.WriteLine($"INTEGRATED: {outcome.RunHead ?? "unknown"} (nothing to integrate)");
                return Integrated;

            case RunIntegrateStatus.Conflict:
                console.WriteLine($"CONFLICT: {string.Join(", ", outcome.Files ?? [])}");
                return Conflict;

            case RunIntegrateStatus.IntegratedWithoutEvidence:
                // The commits are on the run branch, so the digest line must be printed before the failure: a worker
                // that retries the whole task would otherwise duplicate work that already landed.
                console.WriteLine($"INTEGRATED: {outcome.RunHead ?? "unknown"}");
                console.MarkupLine(
                    $"[red]{Markup.Escape(CommandName)}: error:[/] " +
                    Markup.Escape(outcome.Message ?? "the task's scratchpad could not be copied."));
                return EvidenceFailed;

            case RunIntegrateStatus.Failed:
                return Fail(outcome.Message ?? "the task could not be integrated.");

            default:
                return Refuse(outcome.Message ?? "the task could not be integrated.");
        }
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    /// <param name="context">The command context Spectre supplies.</param>
    /// <param name="settings">The parsed settings.</param>
    public int Execute(CommandContext context, RunIntegrateSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private int Refuse(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return RefusedInput;
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return Failed;
    }
}
