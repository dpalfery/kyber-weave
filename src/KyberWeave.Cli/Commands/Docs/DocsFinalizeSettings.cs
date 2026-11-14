using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Settings for <c>docs finalize</c>: the shared docs settings, where the positional path is the plan file, plus the
/// approval being recorded.
/// </summary>
public sealed class DocsFinalizeSettings : DocsSettings
{
    /// <summary>Who approved the plan: <c>autonomous</c> or <c>supervised</c>.</summary>
    [CommandOption("--approval-mode <MODE>")]
    [Description("Who approved the plan: 'autonomous' (the conductor accepted the recommendation) or 'supervised' (the owner approved it).")]
    public string? ApprovalMode { get; set; }

    /// <summary>One line saying what approved the plan, recorded in its Decisions table.</summary>
    [CommandOption("--approval <TEXT>")]
    [Description("One line saying what approved the plan, such as 'AUTONOMOUS_APPROVAL: approve-and-execute' or the owner's answer. Recorded in the Decisions table.")]
    public string? Approval { get; set; }

    /// <summary>The approval date, <c>yyyy-MM-dd</c>. Defaults to today (UTC).</summary>
    [CommandOption("--date <DATE>")]
    [Description("Approval date as yyyy-MM-dd. Defaults to today (UTC).")]
    public string? Date { get; set; }
}
