using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings for <c>arbiter comment</c>: one bot review comment to classify against the repository.</summary>
public sealed class ArbiterCommentSettings : CommandSettings
{
    /// <summary>The repository root the cited path is read under and the decision log is written to.</summary>
    [CommandOption("--root <DIR>")]
    [Description("The repository root the cited path is read under. Defaults to the current directory.")]
    public string Root { get; set; } = ".";

    /// <summary>The repository-relative path the comment cites.</summary>
    [CommandOption("--path <FILE>")]
    [Description("The repository-relative path the comment cites.")]
    public string Path { get; set; } = string.Empty;

    /// <summary>The 1-based line the comment cites.</summary>
    [CommandOption("--line <N>")]
    [Description("The 1-based line the comment cites.")]
    public int Line { get; set; }

    /// <summary>The comment text.</summary>
    [CommandOption("--body <TEXT>")]
    [Description("The comment text.")]
    public string Body { get; set; } = string.Empty;

    /// <summary>The commit the comment was made on, when known.</summary>
    [CommandOption("--commit-sha <SHA>")]
    [Description("The commit the comment was made on, when known.")]
    public string? CommitSha { get; set; }
}
