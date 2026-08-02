using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

public sealed class DocsInitSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Repository root to initialize. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("--docs-root <DIR>")]
    [Description("Documentation root. Honors an existing .kyber-weave/kyber-weave.yml docs-root; otherwise the first existing conventional root (docs, 6-Docs, doc, documentation), else 'docs'.")]
    public string? DocsRoot { get; set; }

    [CommandOption("--owner <OWNER>")]
    [Description("Seed owner for the catalog and scaffolded documents.")]
    [DefaultValue("unassigned")]
    public string Owner { get; set; } = "unassigned";

    [CommandOption("-t|--target <TARGETS>")]
    [Description("APM target harnesses for the authoring skill, comma-separated. 'agent-skills' deploys to .agents/skills/ (cross-client).")]
    [DefaultValue("agent-skills")]
    public string Target { get; set; } = "agent-skills";

    [CommandOption("--no-skill")]
    [Description("Scaffold the corpus only; do not deploy the kyber-weave-docs authoring skill via APM.")]
    public bool NoSkill { get; set; }

    [CommandOption("--force")]
    [Description("Overwrite existing scaffolded documents instead of leaving them alone. Never overwrites .kyber-weave/kyber-weave.yml.")]
    public bool Force { get; set; }
}
