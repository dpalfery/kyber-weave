using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Unit tests pinning <see cref="SquadResourceLinkRewriter"/>: closure-driven rewriting of
/// Markdown link targets, with characterization of its current edge-case behaviour.
/// </summary>
public sealed class SquadResourceLinkRewriterTests
{
    private static readonly HashSet<string> Closure = new(StringComparer.Ordinal)
    {
        "references/x.md",
    };

    private static string Rewrite(
        string body,
        Func<string, string> mapPath,
        out bool changed,
        IReadOnlySet<string>? closure = null) =>
        SquadResourceLinkRewriter.Rewrite(body, closure ?? Closure, mapPath, out changed);

    private static string Root(string path) => $"/root/{path}";

    [Fact]
    public void RewritesPlainLinkInClosure()
    {
        string result = Rewrite("See [x](references/x.md) now.", Root, out bool changed);

        Assert.Equal("See [x](/root/references/x.md) now.", result);
        Assert.True(changed);
    }

    [Fact]
    public void KeepsFragmentSuffix()
    {
        string result = Rewrite("[x](references/x.md#section)", Root, out bool changed);

        Assert.Equal("[x](/root/references/x.md#section)", result);
        Assert.True(changed);
    }

    [Fact]
    public void KeepsQuerySuffix()
    {
        string result = Rewrite("[x](references/x.md?a=1)", Root, out bool changed);

        Assert.Equal("[x](/root/references/x.md?a=1)", result);
        Assert.True(changed);
    }

    [Fact]
    public void LeavesLinkOutsideClosureUnchanged()
    {
        const string body = "[y](references/other.md)";

        string result = Rewrite(body, Root, out bool changed);

        Assert.Equal(body, result);
        Assert.False(changed);
    }

    [Fact]
    public void LeavesExternalUrlUnchanged()
    {
        const string body = "[y](https://example.com/references/x.md)";

        string result = Rewrite(body, Root, out bool changed);

        Assert.Equal(body, result);
        Assert.False(changed);
    }

    [Fact]
    public void LeavesProseMentionOfPathUnchanged()
    {
        const string body = "Read references/x.md before starting.";

        string result = Rewrite(body, Root, out bool changed);

        Assert.Equal(body, result);
        Assert.False(changed);
    }

    [Fact]
    public void WrapsMappedTargetContainingWhitespaceInAngleBrackets()
    {
        string result = Rewrite(
            "[x](references/x.md#frag)",
            p => $"/home/my user/{p}",
            out bool changed);

        Assert.Equal("[x](</home/my user/references/x.md#frag>)", result);
        Assert.True(changed);
    }

    [Fact]
    public void AppliesZCodeStyleMapping()
    {
        string result = Rewrite(
            "[x](references/x.md)",
            p => $"../skills/{p}",
            out bool changed);

        Assert.Equal("[x](../skills/references/x.md)", result);
        Assert.True(changed);
    }

    [Fact]
    public void ReportsNotChangedWhenNothingRewritten()
    {
        string result = Rewrite("no links here", Root, out bool changed);

        Assert.Equal("no links here", result);
        Assert.False(changed);
    }

    [Fact]
    public void RewritesLinkWithTitleAndKeepsTheTitle()
    {
        string result = Rewrite("[x](references/x.md \"t\")", Root, out bool changed);

        Assert.Equal("[x](/root/references/x.md \"t\")", result);
        Assert.True(changed);
    }

    [Fact]
    public void RewritesAuthoredAngleFormLink()
    {
        string result = Rewrite("[x](<references/x.md>)", Root, out bool changed);

        Assert.Equal("[x](/root/references/x.md)", result);
        Assert.True(changed);
    }

    [Fact]
    public void LeavesLinkInsideFencedCodeBlockUntouched()
    {
        const string body = "```\n[x](references/x.md)\n```";

        string result = Rewrite(body, Root, out bool changed);

        Assert.Equal(body, result);
        Assert.False(changed);
    }

    [Fact]
    public void LeavesLinkInsideTildeFenceUntouchedButRewritesLinksAroundIt()
    {
        string result = Rewrite(
            "[a](references/x.md)\n~~~md\n[x](references/x.md)\n~~~\n[b](references/x.md)",
            Root,
            out bool changed);

        Assert.Equal(
            "[a](/root/references/x.md)\n~~~md\n[x](references/x.md)\n~~~\n[b](/root/references/x.md)",
            result);
        Assert.True(changed);
    }

    [Fact]
    public void WrapsMappedTargetContainingParenthesesInAngleForm()
    {
        string result = Rewrite(
            "[x](references/x.md#s)",
            p => $"/home/a(b)/{p}",
            out bool changed);

        Assert.Equal("[x](</home/a(b)/references/x.md#s>)", result);
        Assert.True(changed);
    }
}
