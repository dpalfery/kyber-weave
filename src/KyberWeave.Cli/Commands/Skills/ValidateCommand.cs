using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ValidateCommand(ISkillGovernanceService skills, IAnsiConsole console) : Command<AnalysisSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, AnalysisSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = skills.Validate(settings.Path);

        CommandHelpers.Finish(console, report, settings, "skill validate", "Skill");
        return report.HasErrors ? 1 : 0;
    }
}
