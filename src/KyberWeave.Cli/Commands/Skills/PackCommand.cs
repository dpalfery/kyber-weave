using System.ComponentModel;
using System.IO.Compression;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Validation;
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

public sealed class PackCommand : Command<PackSettings>
{
    public override int Execute(CommandContext context, PackSettings settings)
    {
        string skillFile = Path.Combine(settings.Path, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            AnsiConsole.MarkupLine($"[red]No SKILL.md in '{Markup.Escape(settings.Path)}'.[/]");
            return 2;
        }

        Skill skill = SkillParser.ParseFile(skillFile);

        if (!settings.SkipValidation)
        {
            DiagnosticReport report = new DiagnosticReport();
            report.AddRange(SpecValidator.Validate(skill));
            if (report.HasErrors)
            {
                AnsiConsole.MarkupLine("[red]Refusing to pack: spec validation failed. Run 'kyber-weave skill validate' or pass --skip-validation.[/]");
                ReportRenderer_RenderErrors(report);
                return 1;
            }
        }

        string name = skill.Frontmatter.Name ?? skill.DirectoryName;
        string output = settings.Output ?? $"{name}.zip";
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        // Bundle the SKILL.md at the archive root plus scripts/, references/, assets/ — the
        // shape Copilot Studio accepts for an uploaded Skill .zip.
        using (ZipArchive zip = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(skill.SkillFilePath, "SKILL.md");
            foreach (SkillResource res in skill.Resources)
            {
                zip.CreateEntryFromFile(res.AbsolutePath, res.RelativePath);
            }
        }

        AnsiConsole.MarkupLine($"[green]Packed[/] [bold]{Markup.Escape(name)}[/] → {Markup.Escape(output)} ({skill.Resources.Count} bundled file(s)).");
        return 0;
    }

    private static void ReportRenderer_RenderErrors(DiagnosticReport report)
    {
        foreach (Diagnostic d in report.Items.Where(i => i.Severity is Severity.Error or Severity.Critical))
        {
            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(d.Code)}[/] {Markup.Escape(d.Message)}");
        }
    }
}
