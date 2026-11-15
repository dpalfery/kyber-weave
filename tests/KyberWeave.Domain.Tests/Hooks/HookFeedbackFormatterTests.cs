using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class HookFeedbackFormatterTests
{
    private static RuleOutcome Failure(string id, string output, HookRuleKind kind = HookRuleKind.Lint) =>
        new(id, kind, true, 2, false, output);

    [Fact]
    public void IsEmptyWhenEverythingPassed() =>
        Assert.Equal(string.Empty, HookFeedbackFormatter.Format("a.cs", [new RuleOutcome("r", HookRuleKind.Lint, true, 0, false, "ok")], [], 10, 1000));

    [Fact]
    public void GroupsOutputByRuleAndNamesTheFile()
    {
        string text = HookFeedbackFormatter.Format("src/A.cs", [Failure("ws", "bad indent"), Failure("t", "1 failed", HookRuleKind.Test)], [], 10, 1000);

        Assert.StartsWith("kyber-weave-hooks: 2 check(s) need attention for src/A.cs", text, StringComparison.Ordinal);
        Assert.Contains("[lint] ws: failed (exit 2)\nbad indent", text, StringComparison.Ordinal);
        Assert.Contains("[test] t: failed (exit 2)\n1 failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrimsToTheLineLimitAndSaysHowMuchWasCut()
    {
        string output = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));

        string trimmed = HookFeedbackFormatter.Trim(output, 3, 1000);

        Assert.Equal("line 1\nline 2\nline 3\n... 7 more line(s) not shown", trimmed);
    }

    [Fact]
    public void TrimsToTheByteLimit()
    {
        string trimmed = HookFeedbackFormatter.Trim("aaaaaaaaa\nbbbbbbbbb\nccccccccc", 100, 22);

        Assert.Equal("aaaaaaaaa\nbbbbbbbbb\n... 1 more line(s) not shown", trimmed);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(5, 0)]
    [InlineData(-1, -1)]
    public void KeepsNothingButCountsLinesWhenALimitIsNotPositive(int maxLines, int maxBytes) =>
        Assert.Equal("... 3 more line(s) not shown", HookFeedbackFormatter.Trim("a\r\nb\n\nc", maxLines, maxBytes));

    [Fact]
    public void CapsAHugeOutputWithoutLosingTheLineCount()
    {
        string output = string.Concat(Enumerable.Range(1, 200_000).Select(i => $"line {i}\n"));

        string trimmed = HookFeedbackFormatter.Trim(output, 2, 3000);

        Assert.Equal("line 1\nline 2\n... 199998 more line(s) not shown", trimmed);
    }

    [Fact]
    public void DoesNotKeepALineThatTheByteWindowCutInHalf()
    {
        string trimmed = HookFeedbackFormatter.Trim("ab\n" + new string('x', 10_000), 10, 20);

        Assert.Equal("ab\n... 1 more line(s) not shown", trimmed);
    }

    [Fact]
    public void KeepsAPrefixOfAnOverlongFirstLine()
    {
        string trimmed = HookFeedbackFormatter.Trim(new string('a', 50) + "\nsecond", 10, 12);

        Assert.Equal(new string('a', 11) + "...\n... 1 more line(s) not shown", trimmed);
    }

    [Fact]
    public void CutsAnOverlongFirstLineOnACharacterBoundary()
    {
        string trimmed = HookFeedbackFormatter.Trim("ééééééé", 10, 6);

        Assert.Equal("éé...", trimmed);
    }

    [Fact]
    public void ReportsATimeoutWithoutOutputAndAppendsWarnings()
    {
        RuleOutcome timeout = new("slow", HookRuleKind.Lint, true, -1, true, "partial");

        string text = HookFeedbackFormatter.Format("a.cs", [timeout], ["unknown key 'x'"], 10, 1000);

        Assert.Contains("[lint] slow: timed out, skipped", text, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", text, StringComparison.Ordinal);
        Assert.EndsWith("warning: unknown key 'x'", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(2, false, true)]
    [InlineData(-1, true, true)]
    public void NeedsAttentionWhenTheCommandFailedOrTimedOut(int exitCode, bool timedOut, bool expected) =>
        Assert.Equal(expected, new RuleOutcome("r", HookRuleKind.Lint, true, exitCode, timedOut, string.Empty).NeedsAttention);

    [Fact]
    public void ShowsWarningsEvenWhenNothingFailed() =>
        Assert.Equal("warning: w", HookFeedbackFormatter.Format("a.cs", [], ["w"], 10, 1000));
}
