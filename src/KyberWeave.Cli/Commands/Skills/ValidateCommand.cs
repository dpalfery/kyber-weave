using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ValidateCommand : Command<AnalysisSettings>
{
    public override int Execute(CommandContext context, AnalysisSettings settings)
    {
        DiagnosticReport report = new DiagnosticReport();
        SkillSet? set = CommandHelpers.LoadOrReport(settings.Path, report);

        if (set is not null)
            foreach (Skill skill in set.Skills)
                report.AddRange(SpecValidator.Validate(skill));

        CommandHelpers.Finish(report, settings, "skill validate", "Skill");
        return report.HasErrors ? 1 : 0;
    }
}
