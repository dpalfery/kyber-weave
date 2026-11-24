using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.KwSkills;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.KwSkills;

/// <summary>
/// The KW skill catalog: which skills a technology and a role agent get, in what order, from which
/// pool, and one skill loaded by name. Every test runs over an in-memory file system and fake embedded
/// resources.
/// </summary>
public sealed class KwSkillCatalogTests
{
    private const string ConfigYaml = """
        ontology:
          docs-root: docs
          technologies: [csharp, react, typescript]
        """;

    private const string Manifest = """
        skills:
          - name: shipped-one
            files:
              - SKILL.md
              - references/ref.md
        """;

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeEmbeddedResources _resources = new();
    private readonly string _root;
    private readonly string _home;
    private readonly KwSkillCatalog _catalog;

    public KwSkillCatalogTests()
    {
        _root = Path.Combine(_files.Root, "repo");
        _home = Path.Combine(_files.Root, "home");
        _files.CreateDirectory(_root);
        _files.CreateDirectory(_home);
        _catalog = new KwSkillCatalog(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: _root, homeDirectory: _home),
            _resources,
            _root,
            KyberWeaveConfigAssembler.FromYaml(ConfigYaml));
    }

    [Fact]
    public void ATechnologyListsTheSkillsItsSkillsYmlNames()
    {
        Skill("go-gates", "Run the Go gates in order.");
        Skill("tauri-ref", "Tauri reference.");
        Put("docs/standards/csharp/csharp.skills.yml", "- go-gates\n- tauri-ref\n");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Problems);
        Assert.Equal(
            [
                new KwSkillEntry("go-gates", "Run the Go gates in order.", KwSkillSource.Repository),
                new KwSkillEntry("tauri-ref", "Tauri reference.", KwSkillSource.Repository)
            ],
            list.Entries);
    }

    [Fact]
    public void AMissingSkillsYmlListsNothing()
    {
        Skill("go-gates", "Run the Go gates in order.");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Entries);
        Assert.Empty(list.Problems);
    }

    [Fact]
    public void AnEmptyListListsNothing()
    {
        Put("docs/standards/csharp/csharp.skills.yml", "[]\n");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Entries);
        Assert.Empty(list.Problems);
    }

    [Theory]
    [InlineData("go-gates: yes\n")]
    [InlineData("- name: go-gates\n")]
    [InlineData("just a sentence\n")]
    [InlineData("")]
    [InlineData("- go-gates\n  bad: [\n")]
    [InlineData("- \"  \"\n")]
    public void ASkillsYmlThatIsNotAListOfNamesListsNothingAndReportsWhy(string content)
    {
        Skill("go-gates", "Run the Go gates in order.");
        Put("docs/standards/csharp/csharp.skills.yml", content);

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Entries);
        KwSkillProblem problem = Assert.Single(list.Problems);
        Assert.Contains("csharp.skills.yml", problem.Path, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-SKILL-001", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChildListsItsParentsSkillsThenItsOwnWithoutDuplicates()
    {
        Skill("a", "A.");
        Skill("b", "B.");
        Skill("c", "C.");
        Put("docs/standards/typescript/typescript.standards.md", Standard("typescript"));
        Put("docs/standards/react/react.standards.md", Standard("react", "builds-on: typescript"));
        Put("docs/standards/typescript/typescript.skills.yml", "- a\n- b\n");
        Put("docs/standards/react/react.skills.yml", "- b\n- c\n");

        KwSkillList list = _catalog.SkillsFor("react", null);

        Assert.Equal(["a", "b", "c"], list.Entries.Select(e => e.Name));
    }

    [Fact]
    public void AMalformedParentStandardFrontmatterIsReportedAndListsOnlyTheChildsSkills()
    {
        Skill("a", "A.");
        Skill("b", "B.");
        Put("docs/standards/typescript/typescript.standards.md", Standard("typescript"));
        Put("docs/standards/react/react.standards.md", "---\nbuilds-on: [unclosed\n---\n\n# react\n");
        Put("docs/standards/typescript/typescript.skills.yml", "- a\n");
        Put("docs/standards/react/react.skills.yml", "- b\n");

        KwSkillList list = _catalog.SkillsFor("react", null);

        Assert.Equal(["b"], list.Entries.Select(e => e.Name));
        KwSkillProblem problem = Assert.Single(list.Problems);
        Assert.Contains("react.standards.md", problem.Path, StringComparison.Ordinal);
        Assert.Contains("not listed", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableParentStandardIsReportedInsteadOfFallingThroughSilently()
    {
        Skill("a", "A.");
        Skill("b", "B.");
        Put("docs/standards/react/react.standards.md", Standard("react", "builds-on: typescript"));
        Put("docs/standards/typescript/typescript.skills.yml", "- a\n");
        Put("docs/standards/react/react.skills.yml", "- b\n");
        Unreadable("react.standards.md");

        KwSkillList list = _catalog.SkillsFor("react", null);

        Assert.Equal(["b"], list.Entries.Select(e => e.Name));
        KwSkillProblem problem = Assert.Single(list.Problems);
        Assert.Contains("react.standards.md", problem.Path, StringComparison.Ordinal);
        Assert.Contains("could not be read", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleAgentNameMapsToItsRoleFolder()
    {
        Skill("plan-craft", "Plan craft.");
        Skill("tech-skill", "Tech.");
        Put("docs/standards/csharp/csharp.skills.yml", "- tech-skill\n");
        Put("docs/standards/architect/architect.skills.yml", "- plan-craft\n");

        KwSkillList list = _catalog.SkillsFor("csharp", "architect");

        Assert.Equal(["tech-skill", "plan-craft"], list.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData("code-reviewer")]
    [InlineData("plan-reviewer")]
    [InlineData("review-lens")]
    [InlineData("review-triage")]
    [InlineData("reviewer")]
    public void TheReviewerAliasesMapToReviewer(string agent)
    {
        Skill("review-craft", "Review craft.");
        Put("docs/standards/reviewer/reviewer.skills.yml", "- review-craft\n");

        KwSkillList list = _catalog.SkillsFor(null, agent);

        Assert.Equal(["review-craft"], list.Entries.Select(e => e.Name));
    }

    [Fact]
    public void AnUnmappedAgentHasNoRoleSkills()
    {
        Skill("some-skill", "Some.");
        Put("docs/standards/csharp-dev/csharp-dev.skills.yml", "- some-skill\n");
        Put("docs/standards/reviewer/reviewer.skills.yml", "- some-skill\n");

        Assert.Empty(_catalog.SkillsFor(null, "csharp-dev").Entries);
        Assert.Empty(_catalog.SkillsFor(null, "../reviewer").Entries);
        Assert.Empty(_catalog.SkillsFor(null, "  ").Entries);
        Assert.Empty(_catalog.SkillsFor(null, null).Entries);
    }

    [Fact]
    public void ARepositorySkillWinsOverTheShippedSkillOfTheSameName()
    {
        Shipped("shipped-one", "From the product.", "SHIPPED BODY");
        Skill("shipped-one", "From the repository.", "REPOSITORY BODY");
        Put("docs/standards/csharp/csharp.skills.yml", "- shipped-one\n");

        KwSkillList list = _catalog.SkillsFor("csharp", null);
        KwSkillLoadResult loaded = _catalog.Load("shipped-one");

        Assert.Equal(
            [new KwSkillEntry("shipped-one", "From the repository.", KwSkillSource.Repository)], list.Entries);
        Assert.Equal(KwSkillLoadOutcome.Found, loaded.Outcome);
        Assert.Equal(KwSkillSource.Repository, loaded.Source);
        Assert.Equal("REPOSITORY BODY", loaded.Body.Trim());
        Assert.Equal(Path.Combine(_root, "docs", "standards", "skills", "shipped-one"), loaded.Folder);
        Assert.Empty(loaded.References);
    }

    [Fact]
    public void AShippedSkillResolvesFromTheManifestWhenTheRepositoryHasNone()
    {
        Shipped("shipped-one", "From the product.", "SHIPPED BODY");
        Put("docs/standards/csharp/csharp.skills.yml", "- shipped-one\n");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Equal(
            [new KwSkillEntry("shipped-one", "From the product.", KwSkillSource.Shipped)], list.Entries);
        Assert.Equal(["shipped-one"], _catalog.Names());
    }

    [Fact]
    public void LoadReturnsTheBodyBelowTheFrontmatterTheFolderAndReferencePathsSorted()
    {
        Skill("go-gates", "Run the Go gates.", "# Go gates\n\nRun them in order.");
        string folder = Path.Combine(_root, "docs", "standards", "skills", "go-gates");
        Put("docs/standards/skills/go-gates/references/b.md", "SECRET-B-CONTENT");
        Put("docs/standards/skills/go-gates/references/a.md", "SECRET-A-CONTENT");
        Put("docs/standards/skills/go-gates/notes.txt", "SECRET-NOTES-CONTENT");

        KwSkillLoadResult result = _catalog.Load("go-gates");

        Assert.Equal(KwSkillLoadOutcome.Found, result.Outcome);
        Assert.Equal("# Go gates\n\nRun them in order.", result.Body.Trim());
        Assert.DoesNotContain("description:", result.Body, StringComparison.Ordinal);
        Assert.Equal(folder, result.Folder);
        string[] expected =
        [
            Path.Combine(folder, "notes.txt"),
            Path.Combine(folder, "references", "a.md"),
            Path.Combine(folder, "references", "b.md")
        ];
        Assert.Equal(expected, result.References);
        Assert.DoesNotContain(
            "SECRET", string.Join('\n', result.References.Append(result.Body)), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingAShippedSkillMaterializesItOnceUnderTheHomeFolder()
    {
        Shipped("shipped-one", "From the product.", "SHIPPED BODY", reference: "REF TEXT");

        KwSkillLoadResult first = _catalog.Load("shipped-one");

        Assert.Equal(KwSkillLoadOutcome.Found, first.Outcome);
        Assert.Equal(KwSkillSource.Shipped, first.Source);
        string prefix = Path.Combine(_home, ".kyber-weave", "kw-skills", "shipped-one-");
        Assert.StartsWith(prefix, first.Folder, StringComparison.Ordinal);
        string digest = first.Folder[prefix.Length..];
        Assert.Equal(12, digest.Length);
        Assert.All(digest, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        Assert.Equal([Path.Combine(first.Folder, "references", "ref.md")], first.References);
        Assert.Equal("REF TEXT", _files.ReadAllText(first.References[0]));
        Assert.True(_files.FileExists(Path.Combine(first.Folder, "SKILL.md")));

        _files.FailWhen(op => op.Name.StartsWith("Write", StringComparison.Ordinal) || op.Name == "CreateDirectory");
        KwSkillLoadResult second = _catalog.Load("shipped-one");

        Assert.Equal(first.Folder, second.Folder);
        Assert.Equal(first.References, second.References);
        Assert.Equal("SHIPPED BODY", second.Body.Trim());
    }

    [Theory]
    [InlineData("../SKILL.md")]
    [InlineData("/SKILL.md")]
    [InlineData("sub/../../SKILL.md")]
    [InlineData("a\\b.md")]
    [InlineData("\" SKILL.md\"")]
    [InlineData("references//ref.md")]
    public void AManifestEntryThatIsNotAPlainRelativePathIsRefused(string entry)
    {
        _resources.Add(
            "KwSkills/skills.manifest.yml",
            $"""
            skills:
              - name: shipped-one
                files:
                  - {entry}
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _catalog.Names());

        Assert.Contains("skills.manifest.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a plain relative path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestEntryListedTwiceIsRefused()
    {
        _resources.Add(
            "KwSkills/skills.manifest.yml",
            """
            skills:
              - name: shipped-one
                files:
                  - SKILL.md
                  - SKILL.md
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _catalog.Names());

        Assert.Contains("skills.manifest.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaterializationFailureStillReturnsTheBody()
    {
        Shipped("shipped-one", "From the product.", "SHIPPED BODY");
        _files.FailWhen(
            op => op.Name.StartsWith("Write", StringComparison.Ordinal) || op.Name == "CreateDirectory",
            new IOException("the disk is full"));

        KwSkillLoadResult result = _catalog.Load("shipped-one");

        Assert.Equal(KwSkillLoadOutcome.Found, result.Outcome);
        Assert.Equal("SHIPPED BODY", result.Body.Trim());
        Assert.Equal("(not materialized: the disk is full)", result.Folder);
        Assert.Empty(result.References);
    }

    [Fact]
    public void AnUnknownNameReturnsTheExistingNames()
    {
        Skill("go-gates", "Go.");
        Shipped("shipped-one", "Shipped.", "BODY");

        KwSkillLoadResult result = _catalog.Load("nope");

        Assert.Equal(KwSkillLoadOutcome.Unknown, result.Outcome);
        Assert.Equal(["go-gates", "shipped-one"], result.ExistingNames);
    }

    [Fact]
    public void AMalformedSkillNamesWhatIsMissing()
    {
        Put("docs/standards/skills/no-desc/SKILL.md", "---\nname: no-desc\n---\n\nBody\n");
        Put("docs/standards/skills/no-name/SKILL.md", "---\ndescription: Something.\n---\n\nBody\n");
        Put("docs/standards/skills/neither/SKILL.md", "---\nlicense: MIT\n---\n\nBody\n");

        Assert.Equal(["description"], _catalog.Load("no-desc").MissingFields);
        Assert.Equal(["name"], _catalog.Load("no-name").MissingFields);
        Assert.Equal(["name", "description"], _catalog.Load("neither").MissingFields);
        Assert.Equal(KwSkillLoadOutcome.Malformed, _catalog.Load("no-desc").Outcome);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x/y")]
    [InlineData("x--y")]
    [InlineData("-x")]
    public void ANameThatIsNotASkillNameIsUnknownAndBuildsNoPath(string name)
    {
        Skill("x", "The only one.");
        // The place a traversal would reach if the name were used to build a path.
        Put("docs/standards/x/SKILL.md", "---\nname: x\ndescription: Outside the pool.\n---\n\nOUTSIDE\n");

        KwSkillLoadResult result = _catalog.Load(name);

        Assert.Equal(KwSkillLoadOutcome.Unknown, result.Outcome);
        Assert.Equal(["x"], result.ExistingNames);
    }

    [Fact]
    public void AWhitespaceTrimmedNameIsTheSkillItNames()
    {
        Skill("x", "The only one.", "BODY X");

        KwSkillLoadResult result = _catalog.Load("x ");

        Assert.Equal(KwSkillLoadOutcome.Found, result.Outcome);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void ALinkedReferenceFileIsNotListed()
    {
        Skill("go-gates", "Go.");
        Put("docs/standards/skills/go-gates/real.md", "REAL");
        Put("elsewhere/secret.md", "SECRET");
        string folder = Path.Combine(_root, "docs", "standards", "skills", "go-gates");
        _files.CreateFileLink(Path.Combine(folder, "linked.md"), Path.Combine(_root, "elsewhere", "secret.md"));
        _files.CreateDirectoryLink(Path.Combine(folder, "linked-dir"), Path.Combine(_root, "elsewhere"));

        KwSkillLoadResult result = _catalog.Load("go-gates");

        Assert.Equal([Path.Combine(folder, "real.md")], result.References);
    }

    private void Unreadable(string suffix) =>
        _files.FailWhen(
            operation => operation.Name == "ReadAllText"
                && operation.Path is not null
                && operation.Path.EndsWith(suffix, StringComparison.Ordinal),
            new IOException("denied"));

    [Fact]
    public void AnUnreadableRepositorySkillMdLoadsAsMalformedWithAReasonInsteadOfThrowing()
    {
        Skill("go-gates", "Go.");
        Unreadable("SKILL.md");

        KwSkillLoadResult result = _catalog.Load("go-gates");

        Assert.Equal(KwSkillLoadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.MissingFields);
        Assert.Contains("could not be read", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableRepositorySkillMdIsAProblemWhenListedInsteadOfThrowing()
    {
        Put("docs/standards/csharp/csharp.skills.yml", "- go-gates\n");
        Skill("go-gates", "Go.");
        Unreadable("SKILL.md");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Entries);
        KwSkillProblem problem = Assert.Single(list.Problems);
        Assert.Contains("go-gates", problem.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AShippedSkillMdMissingFromTheProductLoadsAsMalformedInsteadOfThrowing()
    {
        // The manifest lists shipped-one, but its SKILL.md was never embedded: reading it throws.
        _resources.Add("KwSkills/skills.manifest.yml", Manifest);

        KwSkillLoadResult result = _catalog.Load("shipped-one");

        Assert.Equal(KwSkillLoadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.MissingFields);
        Assert.Contains("could not be read", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AShippedSkillMdMissingFromTheProductIsAProblemWhenListedInsteadOfThrowing()
    {
        _resources.Add("KwSkills/skills.manifest.yml", Manifest);
        Put("docs/standards/csharp/csharp.skills.yml", "- shipped-one\n");

        KwSkillList list = _catalog.SkillsFor("csharp", null);

        Assert.Empty(list.Entries);
        KwSkillProblem problem = Assert.Single(list.Problems);
        Assert.Contains("shipped-one", problem.Message, StringComparison.Ordinal);
        Assert.Contains("could not be read", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableFrontmatterLoadsAsMalformedWithTheParserReasonAndNoFieldNames()
    {
        Put("docs/standards/skills/broken/SKILL.md", "---\nname: [unclosed\n---\n\nBody\n");

        KwSkillLoadResult result = _catalog.Load("broken");

        Assert.Equal(KwSkillLoadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.MissingFields);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    private void Put(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private void Skill(string name, string description, string body = "BODY") =>
        Put($"docs/standards/skills/{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");

    private void Shipped(string name, string description, string body, string reference = "REF")
    {
        if (!ManifestAdded)
        {
            _resources.Add("KwSkills/skills.manifest.yml", Manifest);
            ManifestAdded = true;
        }

        _resources.Add(
            $"KwSkills/{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
        _resources.Add($"KwSkills/{name}/references/ref.md", reference);
    }

    private bool ManifestAdded { get; set; }

    private static string Standard(string technology, string extra = "") => $"""
        ---
        id: standards/{technology}
        title: {technology} standard
        doc-type: coding-standard
        status: current
        technology: {technology}
        {extra}
        owner: Maintainers
        last-reviewed: 2026-11-16
        ---

        # {technology}
        """;
}
