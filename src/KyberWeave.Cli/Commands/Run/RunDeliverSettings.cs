using System.ComponentModel;
using KyberWeave.Cli.Commands.Docs;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>
/// Settings for <c>kyber-weave run deliver</c>: the shared docs settings, where the positional path is the run's
/// worktree, plus what the pull request description cannot read from the run.
/// </summary>
public sealed class RunDeliverSettings : DocsSettings
{
    /// <summary>One to three plain sentences: the problem and the outcome.</summary>
    [CommandOption("--summary <TEXT>")]
    [Description("One to three plain sentences for a human reviewer: the problem and the outcome. Required.")]
    public string? Summary { get; set; }

    /// <summary>Extra notes for reviewers.</summary>
    [CommandOption("--notes <TEXT>")]
    [Description("Extra notes for reviewers, such as follow-ups.")]
    public string? Notes { get; set; }

    /// <summary>A title that replaces the composed one.</summary>
    [CommandOption("--title <TEXT>")]
    [Description("A title to use instead of the composed '<type>(<scope>): <summary> [[<harness>]]'.")]
    public string? Title { get; set; }

    /// <summary>Opens a new pull request ready for review instead of as a draft.</summary>
    [CommandOption("--no-draft")]
    [Description("Open a new pull request ready for review instead of as a draft. An existing pull request keeps its state.")]
    public bool NoDraft { get; set; }

    /// <summary>The documentation-impact outcome, as docs-dev returned it.</summary>
    [CommandOption("--documentation-impact <TEXT>")]
    [Description("The documentation-impact outcome as docs-dev returned it.")]
    public string? DocumentationImpact { get; set; }

    /// <summary>The archived plan, when the branch archived more than one.</summary>
    [CommandOption("--plan <FILE>")]
    [Description("The archived plan's file name, only when the branch archived more than one.")]
    public string? Plan { get; set; }
}
