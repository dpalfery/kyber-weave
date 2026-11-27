using System.ComponentModel;
using KyberWeave.Contracts.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class NewSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Skill name (lowercase letters, digits, hyphens). Becomes the folder name.")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("-o|--output <DIR>")]
    [Description("Parent directory to create the skill in. Default: current directory.")]
    [DefaultValue(".")]
    public string Output { get; set; } = ".";

    [CommandOption("-t|--template <TEMPLATE>")]
    [Description("Template: sop | runbook | reference | checklist | blank. Default: blank.")]
    [DefaultValue("blank")]
    public string Template { get; set; } = "blank";

    [CommandOption("--no-license")]
    [Description("Omit the license front-matter field.")]
    public bool NoLicense { get; set; }

    [CommandOption("--no-metadata")]
    [Description("Omit the metadata front-matter block.")]
    public bool NoMetadata { get; set; }
}

public sealed class NewCommand(ISkillGovernanceService skills, IAnsiConsole ansi) : Command<NewSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, NewSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string name = settings.Name;
        SkillScaffoldResult result = skills.Scaffold(name, settings.Output, settings.Template, !settings.NoLicense, !settings.NoMetadata);

        switch (result.Status)
        {
            case SkillScaffoldStatus.InvalidName:
                ansi.MarkupLine($"[red]Invalid name '{Markup.Escape(name)}'. Use lowercase letters, digits and single hyphens.[/]");
                return 2;
            case SkillScaffoldStatus.AlreadyExists:
                ansi.MarkupLine($"[red]Directory already exists: {Markup.Escape(result.Directory)}[/]");
                return 2;
            default:
                ansi.MarkupLine($"[green]Created skill[/] [bold]{Markup.Escape(name)}[/] at {Markup.Escape(result.Directory)}");
                ansi.MarkupLine($"Next: [grey]kyber-weave skill validate {Markup.Escape(result.Directory)} && kyber-weave skill lint {Markup.Escape(result.Directory)} --explain[/]");
                return 0;
        }
    }
}
