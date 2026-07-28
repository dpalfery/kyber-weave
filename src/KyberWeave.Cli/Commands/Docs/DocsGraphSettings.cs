using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

public sealed class DocsGraphSettings : DocsSettings
{
    [CommandOption("-o|--out <DIR>")]
    [Description("Output directory for nodes.jsonl and edges.jsonl.")]
    [DefaultValue("./build/doc-graph")]
    public string Out { get; set; } = "./build/doc-graph";
}
