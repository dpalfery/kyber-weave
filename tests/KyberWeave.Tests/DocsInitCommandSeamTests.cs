using System.Diagnostics;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using KyberWeave.Data.Configuration;
using KyberWeave.Domain.Doctor;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>docs init</c> resolves, runs and prints, over the shared file-system, path, environment, process and
/// console fakes: nothing here touches the disk, starts a process or reads the host's console.
/// </summary>
public sealed class DocsInitCommandSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly ScriptedProcessRunner _processes = new();
    private readonly FakeRepositoryDoctor _doctor = new();
    private readonly StubScaffolder _scaffolder = new();
    private readonly StubWiring _wiring = new();
    private readonly Dictionary<string, string?> _variables = new(StringComparer.Ordinal);

    public DocsInitCommandSeamTests()
    {
        _console.Profile.Width = 400;
        _files.CreateDirectory(Root);
    }

    [Fact]
    public void Execute_ScaffoldFailure_ReportsItAndReturnsOne()
    {
        _scaffolder.Failure = new InvalidDataException("KW-CONFIG-001 broken");

        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true });

        Assert.Equal(1, exit);
        Assert.Equal("Could not scaffold: KW-CONFIG-001 broken\n", Output());
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public void Execute_ScaffoldWithoutSkillOrWiringCheck_PrintsTheReportAndTheNextSteps()
    {
        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true, NoMcpCheck = true });

        Assert.Equal(0, exit);
        Assert.Equal(
            "Documentation root: docs (configured)\n\n" +
            "  created   docs/catalog.md\n\n" +
            "\nMCP wiring not checked (--no-mcp-check).\n" +
            "Next:\n" +
            "  1. Replace the example row in docs/catalog.md with your real components and owners.\n" +
            $"  2. Run kyber-weave docs validate {Root} to see what the corpus needs.\n" +
            "  3. Ask your agent to apply the kyber-docs-authoring skill to the failing documents.\n" +
            "  4. Declare your stacks in ontology.technologies and re-run, to scaffold a coding standard per technology under docs/standards/.\n",
            Output());
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public void Execute_RelativePath_IsResolvedAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        _ = Run(new DocsInitSettings { Path = "repo", NoSkill = true, NoMcpCheck = true, Provision = true });

        DoctorRun run = Assert.Single(_doctor.Runs);
        Assert.Equal(Root, run.Root);
    }

    [Fact]
    public void Execute_Skill_RunsApmInTheRepositoryRootWithTheFiveMinuteBound()
    {
        _ = _processes.Script("apm", new ProcessRunResult(0, string.Empty, TimedOut: false), "install", "dpalfery/kyber-weave", "--target", "CLAUDE");

        int exit = Run(new DocsInitSettings { Path = "repo", Target = "CLAUDE", NoMcpCheck = true });

        Assert.Equal(0, exit);
        ProcessRunRequest call = Assert.Single(_processes.Calls);
        Assert.Equal(Root, call.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(5), call.Timeout);
        Assert.Equal(string.Empty, call.StandardInput);
        Assert.Contains("deployed kyber-docs-authoring skill → target CLAUDE", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ApmExitsNonZero_ReportsItAsSkippedWithTheManualCommand()
    {
        _ = _processes.Script(
            "apm",
            new ProcessRunResult(3, "out", TimedOut: false) { StandardOutput = "out", StandardError = " boom " },
            "install", "dpalfery/kyber-weave", "--target", "CLAUDE");

        int exit = Run(new DocsInitSettings { Path = Root, Target = "CLAUDE", NoMcpCheck = true });

        Assert.Equal(0, exit);
        Assert.Contains("skipped  kyber-docs-authoring skill — apm exited 3: boom.", Output(), StringComparison.Ordinal);
        Assert.Contains("apm install dpalfery/kyber-weave --target CLAUDE", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ApmNotInstalled_ReportsItAsSkipped()
    {
        _ = _processes.WithMissingProgram("apm");

        int exit = Run(new DocsInitSettings { Path = Root, NoMcpCheck = true });

        Assert.Equal(0, exit);
        Assert.Contains("APM is not installed or not on PATH.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ApmTimesOut_ReportsItAsSkipped()
    {
        _ = _processes.ScriptTimeout("apm");

        int exit = Run(new DocsInitSettings { Path = Root, NoMcpCheck = true });

        Assert.Equal(0, exit);
        Assert.Contains("apm did not finish and was stopped after the time limit.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RetiredSkillDirectory_IsWarnedAboutOncePerTarget()
    {
        _files.CreateDirectory("/kw/work/repo/.claude/skills/kyber-weave-docs");
        _files.CreateDirectory("/kw/work/repo/.agents/skills/doc-corpus-integrity-review");

        int exit = Run(new DocsInitSettings { Path = Root, Target = "claude,agent-skills,other", NoSkill = true, NoMcpCheck = true });

        Assert.Equal(0, exit);
        string output = Output();
        Assert.Contains("Retired skill 'kyber-weave-docs' still present at /kw/work/repo/.claude/skills/kyber-weave-docs.", output, StringComparison.Ordinal);
        Assert.Contains("rm -rf \"/kw/work/repo/.claude/skills/kyber-weave-docs\"", output, StringComparison.Ordinal);
        Assert.Contains("Retired skill 'doc-corpus-integrity-review' still present at /kw/work/repo/.agents/skills/doc-corpus-integrity-review.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_NoRetiredSkillDirectory_PrintsNoWarning()
    {
        _ = Run(new DocsInitSettings { Path = Root, Target = "CLAUDE", NoSkill = true, NoMcpCheck = true });

        Assert.DoesNotContain("Retired skill", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Provision_HandsTheDoctorTheEnvironmentsVariablesAndAProcessSeamBoundedAtTenMinutes()
    {
        _variables["KW_PROBE"] = "from-the-fake";
        _ = _processes.Script("probe", new ProcessRunResult(0, string.Empty, TimedOut: false));

        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true, NoMcpCheck = true, Provision = true });

        Assert.Equal(0, exit);
        DoctorRun run = Assert.Single(_doctor.Runs);
        Assert.Equal(DoctorMode.Provision, run.Mode);
        Assert.Equal("from-the-fake", run.Environment("KW_PROBE"));
        Assert.Null(run.Environment("KW_ABSENT"));
        _ = run.Executor.Run(new ProcessStartInfo("probe"), string.Empty);
        ProcessRunRequest call = Assert.Single(_processes.Calls);
        Assert.Equal(TimeSpan.FromSeconds(600), call.Timeout);
    }

    [Fact]
    public void Execute_BrokenConfiguration_ReturnsOneAfterTheScaffold()
    {
        WriteConfig("ontology: [not, a, mapping");

        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true });

        Assert.Equal(1, exit);
        Assert.Contains(KyberWeaveConfigDiagnosticCodes.LoadError, Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_DeclaredServersWithNoFix_PrintsTheWiringSectionAndReturnsZero()
    {
        WriteConfig(McpDeclarationBlockWriter.DeclarationYaml);

        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true });

        Assert.Equal(0, exit);
        Assert.Contains("MCP wiring:", Output(), StringComparison.Ordinal);
        Assert.Contains("wired", Output(), StringComparison.Ordinal);
        Assert.Equal(Root, Assert.Single(_wiring.Inspected));
    }

    [Fact]
    public void Execute_FixesOnAConsoleThatCannotBeAsked_PrintsTheReportAndWritesNothing()
    {
        WriteConfig(McpDeclarationBlockWriter.DeclarationYaml);
        _wiring.Fixes = [new McpWiringFix("claude", "/kw/work/repo/.mcp.json", ["kyber-weave"], Create: true)];

        int exit = Run(new DocsInitSettings { Path = Root, NoSkill = true });

        Assert.Equal(0, exit);
        Assert.False(_wiring.Repaired);
        Assert.DoesNotContain("This writes:", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrintRepairScope_NamesEveryFileHarnessAndServerOnItsOwnLine()
    {
        DocsInitCommand.PrintRepairScope(
            _console,
            [
                new McpWiringFix("claude", "/kw/work/repo/.mcp.json", ["a", "b"], Create: true),
                new McpWiringFix("cursor", "/kw/work/repo/.cursor/mcp.json", ["a"], Create: false),
            ]);

        Assert.Equal(
            "This writes:\n" +
            "  /kw/work/repo/.mcp.json (claude) adding a, b\n" +
            "  /kw/work/repo/.cursor/mcp.json (cursor) adding a\n",
            Output());
    }

    private void WriteConfig(string yaml)
    {
        _files.CreateDirectory("/kw/work/repo/.kyber-weave");
        _files.WriteAllText("/kw/work/repo/.kyber-weave/kyber-weave.yml", yaml);
    }

    private int Run(DocsInitSettings settings)
    {
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory, readVariable: name => _variables.GetValueOrDefault(name));
        FakePathResolver paths = new(_files);
        FileKyberWeaveConfigRepository configs = new(new FileRepositoryTreeProbe(_files), _files, paths, environment);
        DocsInitCommand command = new(_wiring, _scaffolder, _doctor, configs, _console, _files, paths, environment, _processes);
        return command.Execute(null!, settings, CancellationToken.None);
    }

    private string Output() => _console.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class StubScaffolder : IDocsScaffoldingService
    {
        public Exception? Failure { get; set; }

        public ScaffoldResult Scaffold(
            string repoRoot,
            string? docsRoot = null,
            string owner = "unassigned",
            bool force = false,
            bool kyberStandards = false,
            bool reviewGates = true,
            IReadOnlyList<string>? standards = null) =>
            Failure is not null
                ? throw Failure
                : new ScaffoldResult("docs", DocsRootSource.Configuration, [new ScaffoldedFile("docs/catalog.md", ScaffoldOutcome.Created)]);
    }

    private sealed class StubWiring : IMcpWiringService
    {
        public List<string> Inspected { get; } = [];

        public IReadOnlyList<McpWiringFix> Fixes { get; set; } = [];

        public bool Repaired { get; private set; }

        public McpWiringReport Inspect(string repositoryRoot, McpConfig config)
        {
            Inspected.Add(repositoryRoot);
            return new McpWiringReport([]);
        }

        public IReadOnlyList<McpWiringFix> Derive(McpWiringReport report, string repositoryRoot) => Fixes;

        public IReadOnlyList<McpRenameChange> Scan(string repositoryRoot, string? harness = null) => [];

        public McpWiringReport Inspect(string repositoryRoot, McpConfig config, ILaunchCommandResolver resolver) =>
            throw new NotSupportedException();

        public ILaunchCommandResolver ResolverFor(string repositoryRoot) => throw new NotSupportedException();

        public IReadOnlyList<string> Harnesses(IEnumerable<McpWiringFix> fixes) => throw new NotSupportedException();

        public McpWiringRepairReport Repair(
            string repositoryRoot,
            McpConfig config,
            ILaunchCommandResolver resolver,
            IReadOnlyList<string> targets,
            bool dryRun = false)
        {
            Repaired = true;
            throw new NotSupportedException();
        }

        public McpWiringRepairReport Unwire(string repositoryRoot, IReadOnlyList<string> targets) =>
            throw new NotSupportedException();

        public IReadOnlyList<McpRenameChange> Migrate(string repositoryRoot, IReadOnlyList<string> targets, bool dryRun = false) =>
            throw new NotSupportedException();
    }
}
