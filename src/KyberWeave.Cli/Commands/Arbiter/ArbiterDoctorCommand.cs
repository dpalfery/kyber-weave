using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Diagnoses the host's Arbiter installation without writing anything.</summary>
/// <remarks>
/// Raises the provider and override diagnostics (<c>KW-ARB-CONFIG-006</c>,
/// <c>-007</c>, <c>-009</c>, <c>-010</c>), the missing-key warning
/// (<c>KW-ARB-KEY-001</c>), the unignored-log warning (<c>KW-ARB-LOG-001</c>,
/// through <c>git check-ignore -q artifacts/arbiter</c>), the binary warning
/// (<c>KW-ARB-BIN-001</c>), and the guard warning (<c>KW-ARB-GUARD-001</c>, when none of the plan, spec or todo
/// index properties resolves to a path on disk). It also notes the Copilot CLI
/// post-dispatch advisory limitation as information (no diagnostic id: nothing is
/// wrong, so there is nothing to suppress or baseline).
/// </remarks>
public sealed class ArbiterDoctorCommand(
    IArbiterDoctorService doctor,
    ArbiterGatingConsole gating,
    IAnsiConsole console) : Command<ArbiterSettings>
{
    /// <summary><c>artifacts/arbiter/</c> is not ignored by git.</summary>
    public const string LogNotIgnored = IArbiterDoctorService.LogNotIgnored;

    /// <summary><c>kyber-weave-arbiter</c> is missing from <c>PATH</c>, or its version differs from the CLI's.</summary>
    public const string BinaryMissing = IArbiterDoctorService.BinaryMissing;

    /// <summary>No plan, spec or todo index is declared, so the Read guard protects nothing.</summary>
    public const string GuardWithoutIndex = IArbiterDoctorService.GuardWithoutIndex;

    /// <summary>
    /// Model targeting is on for a hooked target whose model targets file is missing, unreadable, or not
    /// the table the last Squad update wrote. Without a current file the model override is dropped without
    /// anyone seeing it.
    /// </summary>
    public const string ModelTargetsMissing = IArbiterDoctorService.ModelTargetsMissing;

    /// <summary>
    /// The project enables Arbiter, and an installed harness is ungated for it: no project hook gates its
    /// dispatches and no live user-level hook does. The finding names the harness and carries its fix.
    /// </summary>
    public const string ProjectUngated = IArbiterDoctorService.ProjectUngated;

    /// <summary>
    /// The global Squad receipt holds an Arbiter hook for a harness that <c>arbiter.global.targets</c> does
    /// not list, so <c>kyber-weave update</c> never refreshes that hook.
    /// </summary>
    public const string GlobalUnlisted = IArbiterDoctorService.GlobalUnlisted;

    /// <summary>
    /// Runs every check and reports findings. A malformed override (<c>-009</c>)
    /// still leaves the product defaults to diagnose, so one bad file never hides
    /// the remaining checks. Writes nothing. Exits 1 only on error findings.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();
        ArbiterDoctorReport inspected = doctor.Inspect(settings.Path, settings.Config, report);
        CommandHelpers.WriteUnfolded(console, () => Print(inspected));

        CommandHelpers.Finish(report, settings, "arbiter doctor", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private void Print(ArbiterDoctorReport inspected)
    {
        console.MarkupLine("[bold]Kyber-Arbiter Doctor[/]");
        console.WriteLine();

        console.MarkupLine("[bold]GLOBAL scope[/]");
        if (inspected.GlobalTargets.Count == 0)
        {
            console.MarkupLine("  [grey]no global targets configured.[/]");
        }
        else
        {
            foreach (ArbiterDoctorScopeTarget target in inspected.GlobalTargets)
            {
                console.MarkupLine($"  {Markup.Escape(target.Label)}: {Markup.Escape(target.Reason)}");
            }
        }

        console.MarkupLine("[bold]PROJECT scope[/]");
        if (inspected.ArbiterEnabled)
        {
            console.MarkupLine("  arbiter.enabled: true");
        }

        gating.WriteRows(inspected.Gating, ArbiterDoctorService.TargetDisplayName);

        foreach (ArbiterDoctorLine line in inspected.Lines)
        {
            console.MarkupLine(Format(line));
        }
    }

    /// <summary>One check line: its mark, then an optional subject and rule id, then the message.</summary>
    private static string Format(ArbiterDoctorLine line)
    {
        string mark = line.Status switch
        {
            ArbiterDoctorStatus.Ok => "[green]ok[/]",
            ArbiterDoctorStatus.Warn => "[yellow]warn[/]",
            _ => "[blue]info[/]",
        };
        string head = (line.Subject, line.RuleId) switch
        {
            (not null, not null) => $"{Markup.Escape(line.Subject)} [bold]{Markup.Escape(line.RuleId)}[/]: ",
            (not null, null) => $"{Markup.Escape(line.Subject)}: ",
            (null, not null) => $"[bold]{Markup.Escape(line.RuleId)}[/]: ",
            _ => string.Empty,
        };
        return $"  {mark} {head}{Markup.Escape(line.Message)}";
    }
}
