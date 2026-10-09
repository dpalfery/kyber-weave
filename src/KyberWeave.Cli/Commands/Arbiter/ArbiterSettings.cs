using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings shared by the read-only Arbiter surfaces.</summary>
/// <remarks>
/// One settings type serves all five verbs so the packet's single settings file
/// holds every flag: <c>validate [path]</c> and <c>plan &lt;file&gt;</c> use the
/// positional, while <c>rules</c>, <c>eval</c> and <c>audit</c> read the options
/// their verb needs and ignore the rest.
/// </remarks>
public class ArbiterSettings : AnalysisSettings
{
    /// <summary>The trigger to list rules for or to evaluate an event as.</summary>
    [CommandOption("--trigger <TRIGGER>")]
    [Description("Arbiter trigger name (for example delegate).")]
    public string? Trigger { get; set; }

    /// <summary>The recorded event file for <c>eval</c>.</summary>
    [CommandOption("--event <FILE>")]
    [Description("Path to an ArbiterEvent JSON file.")]
    public string? Event { get; set; }

    /// <summary>The provider override for <c>eval</c>.</summary>
    [CommandOption("--provider <KIND>")]
    [Description("Provider for eval: none or systemone. Defaults to the configured provider.")]
    public string? Provider { get; set; }

    /// <summary>The plan file filter for <c>audit</c>.</summary>
    [CommandOption("--plan <FILE>")]
    [Description("Only report ledger events carrying this plan file.")]
    public string? Plan { get; set; }

    /// <summary>The session filter for <c>audit</c>.</summary>
    [CommandOption("--session <ID>")]
    [Description("Only report ledger events from this session.")]
    public string? Session { get; set; }

    /// <summary>The lower-bound timestamp filter for <c>audit</c>.</summary>
    [CommandOption("--since <ISO-8601>")]
    [Description("Only report ledger events at or after this ISO-8601 timestamp.")]
    public string? Since { get; set; }
}
