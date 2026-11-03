using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings shared by the Arbiter surfaces.</summary>
/// <remarks>
/// One settings type serves every verb so the packet's single settings file
/// holds each flag: <c>validate [path]</c> and <c>plan &lt;file&gt;</c> use the
/// positional, while <c>rules</c>, <c>eval</c>, <c>audit</c>, <c>setup</c>,
/// <c>status</c> and <c>doctor</c> read the options their verb needs and ignore
/// the rest.
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

    /// <summary>The provider kind: <c>eval</c> overrides the configured provider, <c>setup</c> chooses it.</summary>
    [CommandOption("--provider <KIND>")]
    [Description("Provider kind: none or systemone. Eval overrides the configured provider; setup writes the choice.")]
    public string? Provider { get; set; }

    /// <summary>The provider endpoint for <c>setup</c>.</summary>
    [CommandOption("--endpoint <URL>")]
    [Description("Provider endpoint URL (for example https://api.typesafe.ai/v1 or http://localhost:11434/v1).")]
    public string? Endpoint { get; set; }

    /// <summary>The answering model for <c>setup</c>.</summary>
    [CommandOption("--model <MODEL>")]
    [Description("Answering model (for example jev-1.13.0, nimble or tev1).")]
    public string? Model { get; set; }

    /// <summary>Whether <c>setup</c> reads the TypeSafe key from stdin rather than a masked prompt.</summary>
    [CommandOption("--key-stdin")]
    [Description("Read the TypeSafe key from stdin instead of a masked prompt.")]
    public bool KeyStdin { get; set; }

    /// <summary>The Ollama base URL <c>setup</c> probes for version detection.</summary>
    [CommandOption("--ollama <URL>")]
    [Description("Ollama base URL to probe for version detection. Defaults to the loopback endpoint or http://localhost:11434.")]
    public string? Ollama { get; set; }

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
