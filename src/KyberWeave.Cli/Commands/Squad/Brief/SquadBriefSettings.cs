using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad.Brief;

/// <summary>Settings for <c>kyber-weave squad brief</c>.</summary>
public sealed class SquadBriefSettings : CommandSettings
{
    /// <summary>The agent whose installed reference files are read.</summary>
    [CommandArgument(0, "<agent>")]
    [Description("The agent whose reference files are read, for example conductor or architect.")]
    public string Agent { get; set; } = string.Empty;

    /// <summary>The route or dispatch: intake, plan, finalize, spec or escalation.</summary>
    [CommandOption("--path <PATH>")]
    [Description("The route the run is on, or the dispatch: intake, plan (drafting), finalize, spec or escalation. Unknown keeps every route's sections.")]
    public string? Path { get; set; }

    /// <summary>The autonomy for this run.</summary>
    [CommandOption("--autonomy <MODE>")]
    [Description("supervised or autonomous. Default: the repository's AGENTS.md Config Reg value, else supervised.")]
    public string? Autonomy { get; set; }

    /// <summary>The plan's development mode.</summary>
    [CommandOption("--mode <MODE>")]
    [Description("test-first or standard. Default: the --plan file's development-mode, else both are kept.")]
    public string? Mode { get; set; }

    /// <summary>A plan file whose frontmatter supplies the mode and implies the plan path.</summary>
    [CommandOption("--plan <FILE>")]
    [Description("A plan file, spelled any way 'docs tasks' accepts. Supplies the mode and implies --path plan.")]
    public string? Plan { get; set; }

    /// <summary>The harness the references are installed for.</summary>
    [CommandOption("--harness <TOKEN>")]
    [Description("The harness the reference files are installed for, such as claude or copilot. Default: try every harness.")]
    public string? Harness { get; set; }

    /// <summary>A directory holding the reference files, instead of finding the installed ones.</summary>
    [CommandOption("--from <DIR>")]
    [Description("Read the reference files from this directory instead of the installed ones.")]
    public string? From { get; set; }

    /// <summary>The output format: text or json.</summary>
    [CommandOption("--format <FORMAT>")]
    [DefaultValue("text")]
    [Description("text (default) or json.")]
    public string Format { get; set; } = "text";
}
