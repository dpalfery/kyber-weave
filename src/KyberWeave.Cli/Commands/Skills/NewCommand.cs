using System.ComponentModel;
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

public sealed partial class NewCommand : Command<NewSettings>
{
    public override int Execute(CommandContext context, NewSettings settings)
    {
        string name = settings.Name;
        if (!MyRegex().IsMatch(name))
        {
            AnsiConsole.MarkupLine($"[red]Invalid name '{Markup.Escape(name)}'. Use lowercase letters, digits and single hyphens.[/]");
            return 2;
        }

        string dir = Path.Combine(settings.Output, name);
        if (Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine($"[red]Directory already exists: {Markup.Escape(dir)}[/]");
            return 2;
        }

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "references"));

        string body = Template(settings.Template, name, !settings.NoLicense, !settings.NoMetadata);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);

        AnsiConsole.MarkupLine($"[green]Created skill[/] [bold]{Markup.Escape(name)}[/] at {Markup.Escape(dir)}");
        AnsiConsole.MarkupLine($"Next: [grey]kyber-weave skill validate {Markup.Escape(dir)} && kyber-weave skill lint {Markup.Escape(dir)} --explain[/]");
        return 0;
    }

    private static string Template(string template, string name, bool includeLicense, bool includeMetadata)
    {
        string title = string.Join(' ', name.Split('-').Select(w => char.ToUpper(w[0]) + w[1..]));
        (string? description, string? instructions) = template.ToLowerInvariant() switch
        {
            "sop" => (
                $"Use to perform {title} the same compliant way every time. Use when a request matches this procedure. Do NOT use for unrelated tasks or when approval limits are exceeded.",
                "## When to use\nState the trigger condition precisely.\n\n## Procedure\n1. Step one.\n2. Step two.\n\n## Rules\n- ALWAYS verify policy windows before acting.\n- NEVER exceed the approval limit.\n\n## Example\nWalk through one concrete, end-to-end case."),
            "runbook" => (
                $"Use to run the {title} operational task with defined steps and known failure handling. Use when the operation is requested. Do NOT use for diagnosis-only or read-only questions.",
                "## When to use\nDescribe the operational trigger.\n\n## Steps\n1. Discover.\n2. Act.\n3. Validate.\n\n## Failure handling\n- If a step fails, ALWAYS roll back and report.\n\n## Example\nShow a full run including one failure path."),
            "reference" => (
                $"Use as a reference manual for {title}: schema, fields and how to query them. Use when the agent needs this domain model. Do NOT use to take actions.",
                "## Overview\nDescribe the data model the LLM cannot infer.\n\n## Fields\n| Field | Meaning |\n|---|---|\n\n## How to query\nShow the correct query shape.\n\n## Example\nA worked query and its result."),
            "checklist" => (
                $"Use to run the {title} checklist so required validations are never skipped. Use before the gated action. Do NOT use after the action has completed.",
                "## When to use\nRun before the gated step.\n\n## Checklist\n- [ ] Item one — MUST pass.\n- [ ] Item two — MUST pass.\n\n## Example\nShow the checklist applied to one case."),
            _ => (
                $"Use when … (state the trigger). Do NOT use for … (state the boundary). Replace this with a specific, routable description.",
                "## When to use\n\n## Instructions\n\n## Example\n")
        };

        string licenseBlock = includeLicense ? "license: MIT\n" : string.Empty;
        string metadataBlock = includeMetadata
            ? """
metadata:
  author: your-name
  version: 0.1.0
"""
            : string.Empty;

        return $"""
---
name: {name}
description: {description}
{licenseBlock}{metadataBlock}
---

# {title}

{instructions}
""";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
