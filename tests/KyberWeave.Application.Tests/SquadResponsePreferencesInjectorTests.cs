using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Contract tests for the deploy-time response-preferences injection layer: source resolution,
/// verbatim replacement, and fail-closed validation for entry files.
/// </summary>
public sealed class SquadResponsePreferencesInjectorTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly string _home;

    public SquadResponsePreferencesInjectorTests()
    {
        _home = Path.Combine(_files.Root, "home");
        _files.CreateDirectory(_home);
    }

    private SquadResponseGuidanceReader Reader => new(_files);

    [Fact]
    public void ReadEffective_UsesHomeFileWhenPresentAndNonWhitespace()
    {
        string home = _home;
        string expected = "Use short tool calls and be explicit about assumptions.";
        string path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        _files.WriteAllText(path, expected);

        List<string> warnings = [];
        string actual = Reader.ReadEffective(home, warnings);

        Assert.Equal(expected, actual);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n \r\n")]
    public void ReadEffective_WhenTheHomeFileIsPresentButBlank_UsesTheShippedDefaultAndWarnsExactlyOnce(string homeText)
    {
        // A blank file is operator input that was accepted as empty. Falling back silently would hide that
        // the operator's edit is being ignored, so the run must say so once.
        string home = _home;
        string path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        _files.WriteAllText(path, homeText);

        List<string> warnings = [];
        string actual = Reader.ReadEffective(home, warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        string warning = Assert.Single(warnings);
        Assert.Contains("response-guidance", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadEffective_WhenTheHomeFileIsAbsent_UsesTheShippedDefaultWithoutWarnings()
    {
        List<string> warnings = [];
        string actual = Reader.ReadEffective(_home, warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ReadEffective_WhenTheHomePathIsADirectory_UsesTheShippedDefaultAndWarns()
    {
        // A directory where the file should be is an operator problem the run must report, not a silent
        // absence. File.Exists answers false for a directory, so the source has to name it explicitly.
        _files.CreateDirectory(Path.Combine(_home, SquadResponseGuidanceDeployer.FileName));

        List<string> warnings = [];
        string actual = Reader.ReadEffective(_home, warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        string warning = Assert.Single(warnings);
        Assert.Contains("response-guidance", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadEffective_WhenSourceIsUnreadable_FallsBackToDefaultAndWarnsExactlyOnceWithoutWriting()
    {
        string path = Path.Combine(_home, SquadResponseGuidanceDeployer.FileName);
        const string originalText = "operator text";
        _files.WriteAllText(path, originalText);

        List<string> warnings = [];
        // The file exists but its bytes are refused: a genuine degradable read failure. Only the reader's
        // own read fails, so the assertions still read the file back afterwards.
        int reads = 0;
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && ++reads == 1,
            new IOException("read denied"));
        string actual = Reader.ReadEffective(_home, warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        string warning = Assert.Single(warnings);
        Assert.Contains("response-guidance", warning, StringComparison.OrdinalIgnoreCase);
        Assert.True(_files.FileExists(path), "The source must not delete or replace the home file while it falls back.");
        Assert.Equal(originalText, _files.ReadAllText(path));
    }

    [Fact]
    public void ReadEffective_WhenTheHomeReadFailsAndTheSeamSuppliesTheDefault_ReturnsThatDefaultAndWarnsExactlyOnce()
    {
        string path = Path.Combine(_home, SquadResponseGuidanceDeployer.FileName);
        _files.WriteAllText(path, "operator text");
        const string seamDefault = "default supplied through the read seam";

        List<string> warnings = [];
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText),
            new IOException("read denied"));
        string actual = Reader.ReadEffective(_home, warnings, () => seamDefault);

        Assert.Equal(seamDefault, actual);
        string warning = Assert.Single(warnings);
        Assert.Contains("response-guidance", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadEffective_WhenTheShippedDefaultIsMissing_PropagatesTheTypedExceptionAndDoesNotInventAWarning()
    {
        // A build without its embedded default is a defect a rerun cannot fix. The source must surface the typed
        // exception so the deployer can name it, rather than swallow it into a generic warning here.
        List<string> warnings = [];
        Assert.Throws<SquadShippedGuidanceUnavailableException>(() =>
            Reader.ReadEffective(
                _home,
                warnings,
                () => throw new SquadShippedGuidanceUnavailableException("The embedded default is missing.")));

        Assert.Empty(warnings);
    }

    [Fact]
    public void Inject_ReplacesTheBlockVerbatim_NoCapNoEscapingNoTrimming()
    {
        string largeText = new string('A', 9 * 1024) + "\n- [tool-call] do_work --force\n- verify result\n";
        string original = "prefix\n<response-preferences>old text</response-preferences>\nsuffix";
        SquadRenderResult render = RenderResult(
            ("agents/conductor.md", original),
            ("agents/bug-crusher.md", "<response-preferences>existing</response-preferences>"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(render, largeText);

        Assert.Equal(
            "prefix\n<response-preferences>" + largeText + "</response-preferences>\nsuffix",
            Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()));
        Assert.Equal(
            "<response-preferences>" + largeText + "</response-preferences>",
            Encoding.UTF8.GetString(injected.Files[1].Content.ToArray()));
        Assert.DoesNotContain("old text", Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain("existing", Encoding.UTF8.GetString(injected.Files[1].Content.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_LeavesARolesStandardsFolderAlone_ItsFilesAreNotThatAgentsEntryFile()
    {
        const string standard = "# Conductor standards\n";
        SquadRenderResult render = RenderResult(
            ("agents/conductor.md", "<response-preferences>old</response-preferences>"),
            ("standards/conductor/conductor.standards.md", standard),
            ("standards/conductor/conductor.rules.md", "rules"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(render, "new");

        Assert.Equal(
            "<response-preferences>new</response-preferences>",
            Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()));
        Assert.Equal(standard, Encoding.UTF8.GetString(injected.Files[1].Content.ToArray()));
        Assert.Equal("rules", Encoding.UTF8.GetString(injected.Files[2].Content.ToArray()));
    }

    [Fact]
    public void Inject_RefusesAnEntryFileUnderANestedStandardsFolder()
    {
        // Only the leading standards/ folder holds standards. A file like agents/standards/conductor.md is still an
        // agent entry: skipping it would ship it with no preferences and no diagnostic.
        SquadRenderResult render = RenderResult(
            ("agents/conductor.md", "<response-preferences>old</response-preferences>"),
            ("agents/standards/conductor.md", "# nested"));

        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(render, "new"));
    }

    [Fact]
    public void Inject_UsesDefaultTextForEmptyEffectiveText()
    {
        SquadRenderResult render = RenderResult(("skills/bug-crusher/SKILL.md", "<response-preferences>old</response-preferences>"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(render, string.Empty);

        string content = Encoding.UTF8.GetString(injected.Files[0].Content.ToArray());
        Assert.Contains("<response-preferences>" + SquadResponseGuidanceSource.DefaultText + "</response-preferences>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_InsertsEffectiveTextVerbatim_WhenItContainsRegexReplacementPatterns()
    {
        // Regex.Replace expands $&, $$, ${name} and $1 in a replacement string. The effective text is
        // operator data, so every one of those sequences must reach the file exactly as typed.
        const string effective = "keep $& literal, $$ literal, ${x} literal, $1 literal";
        SquadRenderResult render = RenderResult(("agents/conductor.md", "<response-preferences>old</response-preferences>"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(render, effective);

        Assert.Equal(
            "<response-preferences>" + effective + "</response-preferences>",
            Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()));
    }

    [Fact]
    public void Inject_RejectsEffectiveTextThatClosesAndReopensTheBlock()
    {
        // Text that closes the block and opens a second one would leave two blocks in the entry file.
        // The render must refuse it rather than emit a file that carries two preference blocks.
        const string breakout = "x</response-preferences><response-preferences>y";
        SquadRenderResult render = RenderResult(("agents/conductor.md", "<response-preferences>old</response-preferences>"));

        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(render, breakout));
    }

    [Fact]
    public void Inject_ThrowsWhenATargetRendersFilesWithNoRecognisableEntryLayout()
    {
        // Codex renders only a file whose layout is neither an entry nor a carrier, while Claude carries
        // a block. Skipping codex silently would leave it without response preferences and no diagnostic.
        SquadRenderResult render = new(
            Success: true,
            Files: new List<SquadDeploymentFile>
            {
                new(".claude/agents/conductor.md", Encoding.UTF8.GetBytes("<response-preferences>old</response-preferences>"), "claude"),
                new("weird/layout.txt", Encoding.UTF8.GetBytes("plain"), "codex"),
            },
            Degradations: [],
            Warnings: [],
            Errors: []);

        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(render, "effective"));
    }

    [Theory]
    [InlineData("agents/conductor.md")]
    [InlineData(".zcode/commands/conductor.md")]
    [InlineData("skills/bug-crusher/SKILL.md")]
    [InlineData("skills/role-bug-crusher.md")]
    [InlineData("skills/lowered-agent.md")]
    public void Inject_ThrowsForEntryFilesMissingOrDuplicatingTheResponseBlock(string relativePath)
    {
        // D8: a render with no block anywhere is a pre-block corpus and returns unchanged; a file that
        // duplicates the block in a render that has one still fails closed.
        SquadRenderResult missing = RenderResult((relativePath, "hello world"));
        SquadRenderResult duplicate = RenderResult((relativePath, "<response-preferences>a</response-preferences>\n<response-preferences>b</response-preferences>"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(missing, "effective");
        Assert.Same(missing, injected);
        Assert.Equal("hello world", Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()));

        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(duplicate, "effective"));
    }

    [Fact]
    public void Inject_DoesNotTouchNonEntryFiles_AndThrowsWhenABlockAppearsThere()
    {
        SquadRenderResult unchanged = RenderResult(("docs/readme.md", "<response-preferences>leave me alone</response-preferences>"));
        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(unchanged, "effective"));

        SquadRenderResult nonEntry = RenderResult(("docs/notes.md", "plain text"));
        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(nonEntry, "effective");
        Assert.Equal("plain text", Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()));
    }

    [Fact]
    public void Inject_AcceptsCarrierNamedFilesAndRejectsUnrelatedSkillsWithBlocks()
    {
        SquadRenderResult carrier = RenderResult((".claude/agents/conductor.md", "<response-preferences>old</response-preferences>"));
        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(carrier, "effective");
        Assert.Contains("<response-preferences>effective</response-preferences>", Encoding.UTF8.GetString(injected.Files[0].Content.ToArray()), StringComparison.Ordinal);

        SquadRenderResult unrelated = RenderResult(("skills/architect/SKILL.md", "<response-preferences>bad</response-preferences>"));
        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(unrelated, "effective"));

        SquadRenderResult prefixed = RenderResult(("skills/role-conductor.md", "<response-preferences>ok</response-preferences>"));
        SquadRenderResult prefixedInjected = SquadResponsePreferencesInjector.Inject(prefixed, "effective");
        Assert.Contains("<response-preferences>effective</response-preferences>", Encoding.UTF8.GetString(prefixedInjected.Files[0].Content.ToArray()), StringComparison.Ordinal);

        SquadRenderResult stripped = RenderResult(("agents/bug-crusher.md", "<response-preferences>ok</response-preferences>"));
        SquadRenderResult strippedInjected = SquadResponsePreferencesInjector.Inject(stripped, "effective");
        Assert.Contains("<response-preferences>effective</response-preferences>", Encoding.UTF8.GetString(strippedInjected.Files[0].Content.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_ThrowsWhenTargetCarrierSetDoesNotProduceAnyBlockBearingFile()
    {
        // A block on one carrier and none on its sibling is a partial render, which still fails closed.
        SquadRenderResult partial = RenderResult(
            (".claude/agents/conductor.md", "<response-preferences>old</response-preferences>"),
            (".claude/agents/bug-crusher.md", "bug-crusher without block"));
        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(partial, "effective"));
    }

    private static SquadRenderResult RenderResult(params (string RelativePath, string Content)[] entries)
    {
        List<SquadDeploymentFile> files = entries
            .Select(entry => new SquadDeploymentFile(entry.RelativePath, Encoding.UTF8.GetBytes(entry.Content), "copilot"))
            .ToList();

        return new SquadRenderResult(
            Success: true,
            Files: files,
            Degradations: [],
            Warnings: [],
            Errors: []);
    }
}
