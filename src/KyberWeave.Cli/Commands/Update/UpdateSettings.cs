using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Update;

public sealed class UpdateSettings : CommandSettings
{
    [CommandArgument(0, "[version]")]
    [Description("GitHub Release to install (e.g. 0.2.0 or v0.2.0-rc.1). Defaults to the latest stable release.")]
    public string? Version { get; set; }

    [CommandOption("--release-candidate")]
    [Description("Install the newest GitHub Release, including release-candidate and -dev tags.")]
    public bool ReleaseCandidate { get; set; }

    [CommandOption("--no-mcp")]
    [Description("Replace only the CLI; leave kyber-weave-mcp unchanged.")]
    public bool NoMcp { get; set; }
}
