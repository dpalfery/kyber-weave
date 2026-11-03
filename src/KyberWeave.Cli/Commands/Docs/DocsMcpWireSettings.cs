using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>kyber-weave docs mcp-wire</c>.</summary>
/// <remarks>
/// The surface is the path, the target list and the dry run, and nothing else, because the design
/// fixes it that way (design §5). A wiring repair is explicit consent (5.1, 5.3): the harnesses to
/// write are named rather than inferred from the tree, and <see cref="DryRun"/> is how the same
/// computation is inspected before any of it happens (5.10).
/// </remarks>
public sealed class DocsMcpWireSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Repository root to repair. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("-t|--target <TARGETS>")]
    [Description("Harness slugs to wire, comma-separated: factory, claude-code, cursor, vscode, opencode. Omit to preview; nothing is written without a named harness.")]
    public string Target { get; set; } = string.Empty;

    [CommandOption("--dry-run")]
    [Description("Report the per-file change and write nothing, not even the receipt. Exits what the real run would.")]
    public bool DryRun { get; set; }
}

/// <summary>Settings for <c>kyber-weave docs mcp-unwire</c>.</summary>
/// <remarks>
/// Two options, because removal needs nothing else: it reads the receipt beside the tree rather
/// than the declaration, so a repository whose <c>mcp:</c> section is malformed can still be
/// unwound. See <see cref="DocsMcpUnwireCommand"/> for why that matters.
/// </remarks>
public sealed class DocsMcpUnwireSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Repository root to restore. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("-t|--target <TARGETS>")]
    [Description("Harness slugs to unwind, comma-separated. Omit to list the files the receipt records.")]
    public string Target { get; set; } = string.Empty;
}
