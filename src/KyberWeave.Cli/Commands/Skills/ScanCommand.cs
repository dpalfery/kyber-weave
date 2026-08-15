using System.ComponentModel;
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
    public override int Execute(CommandContext context, ScanSettings settings)
    {
        DiagnosticReport report = new DiagnosticReport();
        SkillSet? set = CommandHelpers.LoadOrReport(settings.Path, report);

        SkillScanner scanner = new SkillScanner();
        if (set is not null)
            foreach (Skill skill in set.Skills)
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
