using System.ComponentModel;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Authoring;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class ScanSettings : AnalysisSettings
{
    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Severity that fails the run: critical | error | warning. Default critical.")]
    [DefaultValue("critical")]
    public string FailOn { get; set; } = "critical";
}

public sealed class ScanCommand(ISkillGovernanceService skills, IAnsiConsole console) : Command<ScanSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, ScanSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = skills.Scan(settings.Path);

        CommandHelpers.Finish(console, report, settings, "skill scan", "Skill");

        return FailOnPolicy.ExitCode(report, settings.FailOn);
    }
}
