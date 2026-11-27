using System.ComponentModel;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class PackSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Path to a single skill directory (containing SKILL.md) to bundle.")]
    public string Path { get; set; } = string.Empty;

    [CommandOption("-o|--output <FILE>")]
    [Description("Output .zip path. Default: <name>.zip in the current directory.")]
    public string? Output { get; set; }

    [CommandOption("--skip-validation")]
    [Description("Pack even if spec validation fails (not recommended).")]
    public bool SkipValidation { get; set; }
}

public sealed class PackCommand(ISkillGovernanceService skills, IAnsiConsole ansi) : Command<PackSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, PackSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SkillPackResult result = skills.Pack(settings.Path, settings.Output, settings.SkipValidation);

        switch (result.Status)
        {
            case SkillPackStatus.NoSkillFile:
                ansi.MarkupLine($"[red]No SKILL.md in '{Markup.Escape(settings.Path)}'.[/]");
                return 2;
            case SkillPackStatus.ValidationFailed:
                ansi.MarkupLine("[red]Refusing to pack: spec validation failed. Run 'kyber-weave skill validate' or pass --skip-validation.[/]");
                ReportRenderer_RenderErrors(result.Report);
                return 1;
            default:
                ansi.MarkupLine($"[green]Packed[/] [bold]{Markup.Escape(result.Name)}[/] → {Markup.Escape(result.Output)} ({result.BundledFiles} bundled file(s)).");
                return 0;
        }
    }

    /// <summary>Prints each error or critical diagnostic, code first.</summary>
    private void ReportRenderer_RenderErrors(DiagnosticReport report)
    {
        foreach (Diagnostic d in report.Items.Where(i => i.Severity is Severity.Error or Severity.Critical))
        {
            ansi.MarkupLine($"  [red]{Markup.Escape(d.Code)}[/] {Markup.Escape(d.Message)}");
        }
    }
}
