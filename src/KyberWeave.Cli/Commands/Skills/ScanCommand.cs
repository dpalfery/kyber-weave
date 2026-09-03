using System.ComponentModel;
using System.Threading;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Security;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ScanSettings : AnalysisSettings
{
    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Severity that fails the run: critical | error | warning. Default critical.")]
    [DefaultValue("critical")]
    public string FailOn { get; set; } = "critical";
}

public sealed class ScanCommand : Command<ScanSettings>
{
    protected override int Execute(CommandContext context, ScanSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();
        SkillSet set = CommandHelpers.LoadOrReport(settings.Path, report);

        SkillScanner scanner = new SkillScanner();
        foreach (Skill skill in set.Skills)
            report.AddRange(scanner.Scan(skill));

        CommandHelpers.Finish(report, settings, "skill scan", "Skill");

        return settings.FailOn.ToUpperInvariant() switch
        {
            "warning" => report.Warnings > 0 || report.HasErrors ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => report.HasCritical ? 1 : 0
        };
    }

    public int Execute(CommandContext context, ScanSettings settings) => Execute(context, settings, CancellationToken.None);
}
