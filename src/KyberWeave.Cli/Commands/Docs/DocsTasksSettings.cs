using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Settings for <c>docs tasks</c>: the shared docs settings, where the positional path is the plan file, or a
/// specification's <c>tasks.md</c>, plus what to do with the task file derived from it.
/// </summary>
public sealed class DocsTasksSettings : DocsSettings
{
    /// <summary>Writes the derived task file beside the plan.</summary>
    [CommandOption("--write")]
    [Description("Write the task file beside the plan. Rewrites it whole; a second run changes nothing.")]
    public bool Write { get; set; }

    /// <summary>Compares the file on disk with the derived one and changes nothing.</summary>
    [CommandOption("--check")]
    [Description("Exit 1 and say what differs when the task file on disk is not exactly what --write would produce.")]
    public bool Check { get; set; }

    /// <summary>Lists the plan's tasks, one line each, and changes nothing.</summary>
    [CommandOption("--list")]
    [Description("Print one line per task: id, role, depends-on, session and file count. Writes nothing.")]
    public bool List { get; set; }
}
