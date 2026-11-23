using KyberWeave.Contracts.Paths;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// The standards migration reads and writes through the file-system, path, environment and identifier seams,
/// so a unit test runs it entirely in memory.
/// </summary>
/// <remarks>
/// The checkout below lives under <c>/kw/work/repo</c> while the root argument is the relative spelling
/// <c>repo</c>: nothing but the injected base directory can find it, which is what proves the migration no
/// longer reads the process working directory.
/// </remarks>
public sealed class StandardsFolderMigrationTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";
    private const string Folder = Root + "/6-Docs/standards/csharp";

    private const string LegacyStandard = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private const string StandardWithOwnHeadings = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        ## Stack

        - .NET 10.

        ## Naming

        Types use PascalCase.

        ## Commands

        Run `dotnet build`.
        """;

    /// <summary>A rules file that exists but whose frontmatter carries no <c>technology</c> line.</summary>
    private const string RulesWithoutTechnology = """
        ---
        id: standards/csharp/rules
        title: C# rules
        doc-type: rule
        status: current
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# rules

        CSHARP-RULES-BODY: keep this line byte for byte.
        """;

    /// <summary>A skills file that says it is a reference rather than a skill.</summary>
    private const string SkillsTypedReference = """
        ---
        id: standards/csharp/skills
        title: C# skills
        doc-type: reference
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# skills

        CSHARP-SKILLS-BODY: keep this line byte for byte.
        """;

    private static readonly KyberWeaveConfig Declared = KyberWeaveConfigDefaults.Product.WithOntology(new()
    {
        Technologies = ["csharp"],
        Roles = ["reviewer"]
    });

    private readonly InMemoryFileSystem _disk = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    public StandardsFolderMigrationTests() => _paths = new FakePathResolver(_disk);

    private StandardsFolderMigration Migration(string repoRoot = "repo") =>
        Migration(repoRoot, Declared);

    private StandardsFolderMigration Migration(string repoRoot, KyberWeaveConfig config) =>
        new(_disk, repoRoot, config, _paths, _environment, new FakeIdGenerator());

    /// <summary>A path seam that records every path a caller resolves, delegating the answer.</summary>
    private sealed class RecordingPathResolver(IPathResolver inner) : IPathResolver
    {
        public List<string> Resolved { get; } = [];

        public string GetFullPath(string path, string baseDirectory)
        {
            string resolved = inner.GetFullPath(path, baseDirectory);
            Resolved.Add(resolved);
            return resolved;
        }

        public bool AreSame(string first, string second) => inner.AreSame(first, second);

        public bool IsWithin(string root, string candidate) => inner.IsWithin(root, candidate);

        public string Canonicalize(string path) => inner.Canonicalize(path);

        public string ResolvePhysicalRoot(string targetRoot) => inner.ResolvePhysicalRoot(targetRoot);
    }

    [Fact]
    public void ItRenamesALegacyStandardUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/README.md", LegacyStandard);

        StandardsFolderMigrationResult result = Migration().Run();

        Assert.Contains(result.Changes, change => change.Kind == StandardsFolderChangeKind.Rename);
        Assert.False(_disk.FileExists($"{Folder}/README.md"));
        Assert.Equal(LegacyStandard, _disk.ReadAllText($"{Folder}/csharp.standards.md"));
    }

    [Fact]
    public void ADryRunPlansTheChangesAndWritesNothing()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/README.md", LegacyStandard);

        StandardsFolderMigrationResult result = Migration().Run(dryRun: true);

        Assert.Contains(result.Changes, change => change.To == "6-Docs/standards/csharp/csharp.standards.md");
        Assert.True(_disk.FileExists($"{Folder}/README.md"));
        Assert.False(_disk.FileExists($"{Folder}/csharp.standards.md"));
        Assert.False(_disk.FileExists($"{Folder}/csharp.rules.md"));
    }

    [Fact]
    public void AStandardKeptAsALinkIsRefusedAndItsTargetIsUntouched()
    {
        _disk.CreateDirectory("/kw/shared");
        _disk.WriteAllText("/kw/shared/csharp-standard.md", StandardWithOwnHeadings);
        _disk.CreateDirectory(Folder);
        _disk.CreateFileLink($"{Folder}/csharp.standards.md", "/kw/shared/csharp-standard.md");

        IOException error = Assert.Throws<IOException>(() => Migration().Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(StandardWithOwnHeadings, _disk.ReadAllText("/kw/shared/csharp-standard.md"));
        Assert.True(_disk.FileExists($"{Folder}/csharp.standards.md"));
    }

    [Fact]
    public void ADanglingLinkWhereAStubWouldBeWrittenIsRefusedAndLeftInPlace()
    {
        _disk.CreateDirectory(Folder);
        _disk.CreateFileLink($"{Folder}/csharp.standards.md", "/kw/missing.md");

        IOException error = Assert.Throws<IOException>(() => Migration().Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            _disk.EnumerateEntries(Folder),
            entry => entry.IsLink && entry.Path.EndsWith("csharp.standards.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ItResolvesAFolderThroughADotSegmentBeforeReadingIt()
    {
        OntologyConfig ontology = new()
        {
            DocsRoots = ["6-Docs/../6-Docs"],
            Technologies = ["csharp"]
        };
        KyberWeaveConfig config = KyberWeaveConfigDefaults.Product.WithOntology(ontology);
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/README.md", LegacyStandard);

        StandardsFolderMigrationResult result = Migration("repo", config).Run();

        Assert.Contains(result.Changes, change => change.Kind == StandardsFolderChangeKind.Rename);
        Assert.False(_disk.FileExists($"{Folder}/README.md"));
        Assert.Equal(LegacyStandard, _disk.ReadAllText($"{Folder}/csharp.standards.md"));
    }

    [Fact]
    public void EveryPathItReadsIsResolvedThroughThePathSeam()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/README.md", LegacyStandard);
        RecordingPathResolver paths = new(_paths);
        StandardsFolderMigration migration =
            new(_disk, "repo", Declared, paths, _environment, new FakeIdGenerator());

        migration.Run(dryRun: true);

        Assert.Contains($"{Folder}/README.md", paths.Resolved);
    }

    [Fact]
    public void ASecondRunPlansNothingAtAll()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/README.md", LegacyStandard);
        Migration().Run();

        StandardsFolderMigrationResult second = Migration().Run();

        Assert.Empty(second.Changes);
    }

    /// <summary>
    /// A rules or skills file the folder already holds is not skipped when its frontmatter does
    /// not declare the folder's doc-type and scope: the migration sets those two lines and leaves
    /// every other frontmatter line and the whole body exactly as it was.
    /// </summary>
    [Fact]
    public void ItRepairsAnExistingRulesAndSkillsFilesFrontmatterAndKeepsTheirText()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/csharp.standards.md", LegacyStandard);
        _disk.WriteAllText($"{Folder}/csharp.rules.md", RulesWithoutTechnology);
        _disk.WriteAllText($"{Folder}/csharp.skills.md", SkillsTypedReference);
        _disk.WriteAllText($"{Folder}/csharp.arbiter-rules.yml", "rules: []\n");

        StandardsFolderMigrationResult result = Migration().Run();

        Assert.Contains(
            result.Changes,
            change => change.Kind == StandardsFolderChangeKind.Frontmatter
                      && change.To == "6-Docs/standards/csharp/csharp.rules.md");
        Assert.Contains(
            result.Changes,
            change => change.Kind == StandardsFolderChangeKind.Frontmatter
                      && change.To == "6-Docs/standards/csharp/csharp.skills.md");

        string expectedRules = RulesWithoutTechnology.Replace(
            "last-reviewed: 2026-11-16\n---",
            "last-reviewed: 2026-11-16\ntechnology: csharp\n---",
            StringComparison.Ordinal);
        Assert.Equal(expectedRules, _disk.ReadAllText($"{Folder}/csharp.rules.md"));

        string expectedSkills = SkillsTypedReference.Replace(
            "doc-type: reference", "doc-type: skill", StringComparison.Ordinal);
        Assert.Equal(expectedSkills, _disk.ReadAllText($"{Folder}/csharp.skills.md"));

        Assert.Empty(Migration().Run().Changes);
    }

    [Fact]
    public void ADryRunPlansTheFrontmatterRepairAndWritesNothing()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/csharp.standards.md", LegacyStandard);
        _disk.WriteAllText($"{Folder}/csharp.rules.md", RulesWithoutTechnology);
        _disk.WriteAllText($"{Folder}/csharp.skills.md", SkillsTypedReference);
        _disk.WriteAllText($"{Folder}/csharp.arbiter-rules.yml", "rules: []\n");

        StandardsFolderMigrationResult result = Migration().Run(dryRun: true);

        Assert.Contains(
            result.Changes,
            change => change.Kind == StandardsFolderChangeKind.Frontmatter
                      && change.To.EndsWith("csharp.rules.md", StringComparison.Ordinal));
        Assert.Equal(RulesWithoutTechnology, _disk.ReadAllText($"{Folder}/csharp.rules.md"));
        Assert.Equal(SkillsTypedReference, _disk.ReadAllText($"{Folder}/csharp.skills.md"));
    }
}
