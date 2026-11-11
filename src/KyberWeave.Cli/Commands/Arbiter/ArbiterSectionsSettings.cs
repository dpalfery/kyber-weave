using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings for <c>arbiter sections</c>: the document to split and the task to select sections for.</summary>
public sealed class ArbiterSectionsSettings : CommandSettings
{
    /// <summary>The markdown document to split into sections.</summary>
    [CommandOption("--path <FILE>")]
    [Description("The markdown document to split into sections.")]
    public string Path { get; set; } = string.Empty;

    /// <summary>The task the sections are selected for.</summary>
    [CommandOption("--task <TEXT>")]
    [Description("The task the sections are selected for, in plain words.")]
    public string Task { get; set; } = string.Empty;
}
