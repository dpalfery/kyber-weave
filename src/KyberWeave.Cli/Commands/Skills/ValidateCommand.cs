using Spectre.Console.Cli;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Cli.Commands;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ValidateCommand : Command<AnalysisSettings>
{
    public override int Execute(CommandContext context, AnalysisSettings settings)
    {
        var report = new DiagnosticReport();
        var set = CommandHelpers.LoadOrReport(settings.Path, report);

        if (set is not null)
            foreach (var skill in set.Skills)
                report.AddRange(SpecValidator.Validate(skill));

        CommandHelpers.Finish(report, settings, "skill validate", "Skill");
        return report.HasErrors ? 1 : 0;
    }
}
