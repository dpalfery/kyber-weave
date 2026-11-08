using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>Settings for <c>kyber-weave run start</c>.</summary>
public sealed class RunStartSettings : CommandSettings
{
    /// <summary>
    ///     The harness the run belongs to: a Kyber-Squad catalogue token, or <c>manual</c> for work named
    ///     after no harness. The token becomes the first segment of the branch.
    /// </summary>
    [CommandOption("--harness <TOKEN>")]
    [Description(
        "The harness the run belongs to: a Squad catalogue token such as claude or commandcode, or " +
        "'manual' for work named after no harness. Becomes the branch's first segment.")]
    public string? Harness { get; set; }

    /// <summary>The conventional-commit type the work is, which becomes the branch's second segment.</summary>
    [CommandOption("--type <TYPE>")]
    [Description(
        "The conventional-commit type the work is: build, chore, ci, docs, feat, fix, perf, refactor, " +
        "revert, style or test. Becomes the branch's second segment.")]
    public string? Type { get; set; }

    /// <summary>The work's slug, which becomes the branch's third segment.</summary>
    [CommandOption("--slug <SLUG>")]
    [Description(
        "The work's slug in lowercase hyphen-separated words, e.g. arbiter-global-block-identity. " +
        "Becomes the branch's third segment.")]
    public string? Slug { get; set; }

    /// <summary>The existing branch a resumed plan or pull request continues on.</summary>
    [CommandOption("--resume <BRANCH>")]
    [Description(
        "Continue on an existing local or origin branch instead of creating one, and report whether it " +
        "is behind the base.")]
    public string? Resume { get; set; }

    /// <summary>The version of the binary that failed, for the three-way version comparison.</summary>
    [CommandOption("--failing-version <VERSION>")]
    [Description(
        "The version of the build that failed, printed beside HEAD and the remote base so an operator " +
        "can see the defect is not the commit they are looking at.")]
    public string? FailingVersion { get; set; }

    /// <summary>Print the run's facts as one JSON object instead of the human-readable report.</summary>
    [CommandOption("--json")]
    [Description("Print the run's facts as one JSON object instead of the human-readable report.")]
    public bool Json { get; set; }
}
