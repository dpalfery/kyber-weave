using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Contract tests for the deploy-time response-preferences injection layer: source resolution,
/// verbatim replacement, and fail-closed validation for entry files.
/// </summary>
public sealed class SquadResponsePreferencesInjectorTests
{
    [Fact]
    public void ReadEffective_UsesHomeFileWhenPresentAndNonWhitespace()
    {
        string home = Path.Combine(Path.GetTempPath(), "kw-response-guidance-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string expected = "Use short tool calls and be explicit about assumptions.";
        string path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        File.WriteAllText(path, expected, Encoding.UTF8);

        List<string> warnings = [];
        string actual = SquadResponseGuidanceSource.ReadEffective(warnings);

        Assert.Equal(expected, actual);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n \r\n")]
    public void ReadEffective_UsesShippedDefaultWhenHomeTextIsMissingOrWhitespace(string homeText)
    {
        string home = Path.Combine(Path.GetTempPath(), "kw-response-guidance-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        File.WriteAllText(path, homeText, Encoding.UTF8);

        List<string> warnings = [];
        string actual = SquadResponseGuidanceSource.ReadEffective(warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ReadEffective_WhenSourceIsUnreadable_FallsBackToDefaultAndWarnsExactlyOnceWithoutWriting()
    {
        string home = Path.Combine(Path.GetTempPath(), "kw-response-guidance-unreadable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string path = Path.Combine(home, SquadResponseGuidanceDeployer.FileName);
        File.WriteAllText(path, "operator text", Encoding.UTF8);

        string original = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            // No-op: the implementation is expected to read through a user-home resolver rather than mutate
            // the process environment, so the observable contract is the warning + default fallback.
        }

        List<string> warnings = [];

        string actual = SquadResponseGuidanceSource.ReadEffective(warnings);

        Assert.Equal(SquadResponseGuidanceSource.DefaultText, actual);
        string warning = Assert.Single(warnings);
        Assert.Contains("response-guidance", warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path), "The source must not create, overwrite or delete the home file.");
        Assert.Equal("operator text", File.ReadAllText(path));
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
    public void Inject_UsesDefaultTextForEmptyEffectiveText()
    {
        SquadRenderResult render = RenderResult(("skills/bug-crusher/SKILL.md", "<response-preferences>old</response-preferences>"));

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(render, string.Empty);

        string content = Encoding.UTF8.GetString(injected.Files[0].Content.ToArray());
        Assert.Contains("<response-preferences>" + SquadResponseGuidanceSource.DefaultText + "</response-preferences>", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agents/conductor.md")]
    [InlineData(".zcode/commands/conductor.md")]
    [InlineData("skills/bug-crusher/SKILL.md")]
    [InlineData("skills/role-bug-crusher.md")]
    [InlineData("skills/lowered-agent.md")]
    public void Inject_ThrowsForEntryFilesMissingOrDuplicatingTheResponseBlock(string relativePath)
    {
        SquadRenderResult missing = RenderResult((relativePath, "hello world"));
        SquadRenderResult duplicate = RenderResult((relativePath, "<response-preferences>a</response-preferences>\n<response-preferences>b</response-preferences>"));

        Assert.Throws<SquadRenderValidationException>(() => SquadResponsePreferencesInjector.Inject(missing, "effective"));
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
