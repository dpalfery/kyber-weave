using KyberWeave.Core.Diagnostics;
using System.Threading;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ValidateCommand : Command<AnalysisSettings>
{
    protected override int Execute(CommandContext context, AnalysisSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();
        SkillSet set = CommandHelpers.LoadOrReport(settings.Path, report);

        foreach (Skill skill in set.Skills)
            report.AddRange(SpecValidator.Validate(skill));

        CommandHelpers.Finish(report, settings, "skill validate", "Skill");
        return report.HasErrors ? 1 : 0;
    }

    public int Execute(CommandContext context, AnalysisSettings settings) => Execute(context, settings, CancellationToken.None);
}
