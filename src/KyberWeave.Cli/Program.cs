using System.Reflection;
using KyberWeave.Cli.Commands.Agents;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Skills;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Update;
using Spectre.Console.Cli;

CommandApp app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("kyber-weave");
    config.SetApplicationVersion($"kyber-weave {GetVersion()}");

    // Every artifact that shapes agent behaviour is governed the same way, so the three
    // branches are deliberately symmetric: one per artifact class.

    // Skill Governance Command Branch
    config.AddBranch("skill", skill =>
    {
        skill.SetDescription("Author, validate and audit SKILL.md Agent Skills.");

        skill.AddCommand<ValidateCommand>("validate")
            .WithDescription("Check skills against the Agent Skills open-format spec.")
            .WithExample("skill", "validate", "./skills");

        skill.AddCommand<LintCommand>("lint")
            .WithDescription("Score routing-readiness and detect name/description collisions.")
            .WithExample("skill", "lint", "./skills", "--explain");

        skill.AddCommand<ScanCommand>("scan")
            .WithDescription("Scan skills as a trust surface (injection, secrets, risky scripts).")
            .WithExample("skill", "scan", "./skills", "--format", "sarif");

        skill.AddCommand<RouteCommand>("route")
            .WithDescription("Simulate which skill fires for a prompt, or run a routing eval set.")
            .WithExample("skill", "route", "\"reset my password\"", "--skills", "./skills")
            .WithExample("skill", "route", "--eval", "./routing-tests.yml", "--skills", "./skills");

        skill.AddCommand<CatalogCommand>("catalog")
            .WithDescription("Inventory all skills under a path (governance view).")
            .WithExample("skill", "catalog", "./skills");

        skill.AddCommand<PackCommand>("pack")
            .WithDescription("Bundle a skill into a Copilot Studio-compatible .zip.")
            .WithExample("skill", "pack", "./skills/password-reset");

        skill.AddCommand<NewCommand>("new")
            .WithDescription("Scaffold a spec-correct skill from a template.")
            .WithExample("skill", "new", "password-reset", "--template", "sop");
    });

    // Agent Harness Governance Command Branch
    config.AddBranch("agent", agent =>
    {
        agent.SetDescription("Manage and audit AI coding harness agent definitions (.codex, .cursor, .claude, etc.).");

        agent.AddCommand<AgentValidateCommand>("validate")
            .WithDescription("Validate harness agent manifests discovered as .harness/agents under the project root.")
            .WithExample("agent", "validate", ".")
            .WithExample("agent", "validate", ".", "--harness", "cursor");

        agent.AddCommand<AgentScanCommand>("scan")
            .WithDescription("Scan agent prompts as a trust surface (secrets, safety-bypass directives).")
            .WithExample("agent", "scan", ".", "--format", "sarif")
            .WithExample("agent", "scan", ".", "--harness", "claude", "--format", "sarif");

        agent.AddCommand<AgentSyncCheckCommand>("sync-check")
            .WithDescription("Verify role parity and instruction drift across harness folders.")
            .WithExample("agent", "sync-check", ".");

        agent.AddCommand<AgentCatalogCommand>("catalog")
            .WithDescription("Display the role x harness governance matrix.");
    });

    // Documentation Graph Governance Command Branch
    config.AddBranch("docs", docs =>
    {
        docs.SetDescription("Validate documentation frontmatter and its joins to the code graph.");

        docs.AddCommand<DocsInitCommand>("init")
            .WithDescription("Scaffold host config, the catalog, and the ontology reference; deploy the authoring skill via APM.")
            .WithExample("docs", "init", ".")
            .WithExample("docs", "init", ".", "--docs-root", "docs", "--target", "claude,agent-skills");

        docs.AddCommand<DocsValidateCommand>("validate")
            .WithDescription("Check documentation frontmatter against the ontology schema (KW-DOC-SPEC-*).")
            .WithExample("docs", "validate", ".");

        docs.AddCommand<DocsDriftCommand>("drift")
            .WithDescription("Resolve documented code references against the CodeGraph index (KW-DOC-DRIFT-*).")
            .WithExample("docs", "drift", ".");

        docs.AddCommand<DocsExportGraphCommand>("export-graph")
            .WithDescription("Export the documentation graph as nodes.jsonl and edges.jsonl.")
            .WithExample("docs", "export-graph", ".", "--out", "./build/doc-graph");

        docs.AddCommand<DocsCatalogCommand>("catalog")
            .WithDescription("Display doc-type coverage by component.")
            .WithExample("docs", "catalog", ".");

        docs.AddCommand<DocsIntegrityCheckCommand>("integrity-check")
            .WithDescription("Find duplicate claims, potential conflicts, and ambiguous terminology.")
            .WithExample("docs", "integrity-check", ".", "--format", "sarif")
            .WithExample("docs", "integrity-check", ".", "--fail-on", "warning");

        docs.AddBranch("review", review =>
        {
            review.SetDescription("Exchange bounded documentation candidates and reusable verdicts.");
            review.AddCommand<DocsReviewExportCommand>("export")
                .WithDescription("Export pending documentation analysis candidates for agent review.")
                .WithExample("docs", "review", "export", ".", "--out", "candidates.json");
            review.AddCommand<DocsReviewImportCommand>("import")
                .WithDescription("Validate and atomically cache an agent verdict bundle.")
                .WithExample("docs", "review", "import", ".", "--in", "verdicts.json");
        });

        docs.AddCommand<DocsGlossaryCommand>("glossary")
            .WithDescription("Preview or merge managed glossary proposals from terminology analysis.")
            .WithExample("docs", "glossary", ".")
            .WithExample("docs", "glossary", ".", "--write");
    });

    // Kyber-Squad Unified Agent and Skill Deployment Command Branch
    config.AddBranch("squad", squad =>
    {
        squad.SetDescription("Manage unified agent and skill deployments across coding harnesses.");

        squad.AddCommand<SquadInstallCommand>("install")
            .WithDescription("Deploy canonical agents and skills to coding harness directories.")
            .WithExample("squad", "install", ".")
            .WithExample("squad", "install", "--target", "codex,cursor")
            .WithExample("squad", "install", "--global");

        squad.AddCommand<SquadUpdateCommand>("update")
            .WithDescription("Update an existing Squad deployment while preserving managed local edits.")
            .WithExample("squad", "update", ".")
            .WithExample("squad", "update", "--replace-managed")
            .WithExample("squad", "update", "--global");

        squad.AddCommand<SquadUninstallCommand>("uninstall")
            .WithDescription("Remove deployed files and state recorded in the ownership receipt.")
            .WithExample("squad", "uninstall", ".")
            .WithExample("squad", "uninstall", "--dry-run")
            .WithExample("squad", "uninstall", "--global");

        squad.AddCommand<SquadStatusCommand>("status")
            .WithDescription("Verify deployed files and report local drift against the ownership receipt.")
            .WithExample("squad", "status", ".")
            .WithExample("squad", "status", "--global");

        squad.AddCommand<SquadDoctorCommand>("doctor")
            .WithDescription("Diagnose prerequisites, toolchain availability, and deployment health.")
            .WithExample("squad", "doctor")
            .WithExample("squad", "doctor", "--global");

        squad.AddCommand<SquadPackCommand>("pack")
            .WithDescription("Package canonical agents and skills into APM and plugin distribution archives.")
            .WithExample("squad", "pack", "--format", "all", "--out", "./dist");
    });

    // Distribution: replace the running Release binaries. Not an artifact-class branch.
    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Replace the installed CLI and MCP binaries from a GitHub Release.")
        .WithExample("update")
        .WithExample("update", "--release-candidate")
        .WithExample("update", "0.2.0")
        .WithExample("update", "--no-mcp");
});

return app.Run(args);

static string GetVersion()
{
    Assembly assembly = typeof(Program).Assembly;
    string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(infoVersion))
    {
        var plusIndex = infoVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
    }

    Version? nameVersion = assembly.GetName().Version;
    if (nameVersion is not null)
    {
        return nameVersion.ToString();
    }

    return "0.0.0";
}
