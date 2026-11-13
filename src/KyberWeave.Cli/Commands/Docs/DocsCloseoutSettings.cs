using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Settings for <c>docs closeout</c>: the shared docs settings, where the positional path is the plan file, plus
/// whether to archive it.
/// </summary>
public sealed class DocsCloseoutSettings : DocsSettings
{
    /// <summary>Moves the plan to the archive instead of only checking it.</summary>
    [CommandOption("--archive")]
    [Description("Archive the plan: set its status, move it to the archive, remove its task file and update both indexes. " +
                 "Without it nothing changes and the command reports whether the closeout evidence is complete.")]
    public bool Archive { get; set; }

    /// <summary>Previews the archive: reports every missing condition and every change, and changes nothing.</summary>
    [CommandOption("--dry-run")]
    [Description("With --archive, report every missing closeout condition and every change that would be made " +
                 "(including a missing archive inventory that would be created), and change nothing.")]
    public bool DryRun { get; set; }

    /// <summary>The archive date, <c>yyyy-MM-dd</c>. Defaults to today (UTC).</summary>
    [CommandOption("--date <DATE>")]
    [Description("Archive date as yyyy-MM-dd. Defaults to today (UTC).")]
    public string? Date { get; set; }
}
