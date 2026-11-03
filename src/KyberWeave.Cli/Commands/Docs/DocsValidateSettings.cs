using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>docs validate</c>: the shared docs settings plus the merge gate.</summary>
public sealed class DocsValidateSettings : DocsSettings
{
    /// <summary>
    /// Adds the merge gate: open work and missing archive closeout are both errors.
    /// Off by default because open work is correct while a build is under way; the pull
    /// request's CI turns it on.
    /// </summary>
    [CommandOption("--merge-ready")]
    [Description(
        "Also fail on open work (KW-DOC-LIFECYCLE-003) and missing archive closeout (KW-DOC-LIFECYCLE-004). Use in pull-request CI.")]
    public bool MergeReady { get; set; }

    /// <summary>
    /// Names the ref the archive-closeout gate diffs the working tree against.
    /// </summary>
    [CommandOption("--base <REF>")]
    [Description(
        "Git ref the working tree is diffed against for the archive-closeout gate (KW-DOC-LIFECYCLE-004). Defaults to origin/HEAD.")]
    public string? Base { get; set; }
}
