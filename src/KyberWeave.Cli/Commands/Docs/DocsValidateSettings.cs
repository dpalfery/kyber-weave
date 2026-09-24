using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

public sealed class DocsValidateSettings : DocsSettings
{
    /// <summary>
    /// Adds the merge gate: a plan or specification still in its active folder is an error.
    /// Off by default because open work is correct while a build is under way; the pull
    /// request's CI turns it on.
    /// </summary>
    [CommandOption("--merge-ready")]
    [Description("Also fail on any plan or specification still open (KW-DOC-LIFECYCLE-003). Use in pull-request CI.")]
    public bool MergeReady { get; set; }
}
