using System.ComponentModel;
using Spectre.Console.Cli;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Security;
using KyberWeave.Cli.Commands;

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
    public override int Execute(CommandContext context, ScanSettings settings)
    {
        var report = new DiagnosticReport();
        var set = CommandHelpers.LoadOrReport(settings.Path, report);

        var scanner = new SkillScanner();
        if (set is not null)
            foreach (var skill in set.Skills)
                report.AddRange(scanner.Scan(skill));

        CommandHelpers.Finish(report, settings, "skill scan", "Skill");

        return settings.FailOn.ToLowerInvariant() switch
        {
            "warning" => report.Warnings > 0 || report.HasErrors ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => report.HasCritical ? 1 : 0
        };
    }
}
