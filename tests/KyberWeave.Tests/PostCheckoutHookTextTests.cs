using KyberWeave.Core.Doctor;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the pure text edits to the shared post-checkout hook: where the marked block lands, what a stale block
/// becomes, and which hooks are refused rather than rewritten.
/// </summary>
/// <remarks>
/// The edits run no I/O. The hook is one file shared by every worktree of a clone, so a bad edit breaks checkout for
/// all of them, and the refusals are what stop an edit rewriting a hook its owner wrote by hand.
/// </remarks>
public sealed class PostCheckoutHookTextTests
{
    private const string Shebang = "#!/bin/sh\n";

    [Fact]
    public void InstallOnAMissingHookWritesTheShebangThenTheBlockWithLfOnly()
    {
        HookTextEdit edit = PostCheckoutHookText.Install(null);

        Assert.Equal(HookTextOutcome.Created, edit.Outcome);
        string text = edit.Text ?? string.Empty;
        Assert.StartsWith(Shebang, text, StringComparison.Ordinal);
        Assert.Equal(Shebang.Length, text.IndexOf(PostCheckoutHookText.Block, StringComparison.Ordinal));
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallOverItsOwnOutputIsUnchangedWithIdenticalText()
    {
        string created = PostCheckoutHookText.Install(null).Text ?? string.Empty;

        HookTextEdit again = PostCheckoutHookText.Install(created);

        Assert.Equal(HookTextOutcome.Unchanged, again.Outcome);
        Assert.Equal(created, again.Text ?? created);
    }

    [Fact]
    public void InstallOnABashHookPutsTheBlockDirectlyAfterTheShebangAndKeepsEveryOriginalLineInOrder()
    {
        const string original = "#!/bin/bash\necho one\necho two\nexit 0\n";

        HookTextEdit edit = PostCheckoutHookText.Install(original);

        Assert.Equal(HookTextOutcome.Inserted, edit.Outcome);
        string text = edit.Text ?? string.Empty;
        Assert.StartsWith("#!/bin/bash\n", text, StringComparison.Ordinal);
        Assert.Equal("#!/bin/bash\n".Length, text.IndexOf(PostCheckoutHookText.Block, StringComparison.Ordinal));

        string[] originalLines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] keptLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => originalLines.Contains(line, StringComparer.Ordinal))
            .ToArray();
        Assert.Equal(originalLines, keptLines);
    }

    [Fact]
    public void InstallReplacesAStaleBlockInPlaceAndLeavesTheTextOutsideItIdentical()
    {
        string[] blockLines = PostCheckoutHookText.Block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string begin = blockLines[0];
        string end = blockLines[^1];
        const string before = "#!/bin/sh\necho before\n";
        const string after = "\necho after\n";
        string existing = before + begin + "\necho stale\n" + end + after;

        HookTextEdit edit = PostCheckoutHookText.Install(existing);

        Assert.Equal(HookTextOutcome.Updated, edit.Outcome);
        string text = edit.Text ?? string.Empty;
        Assert.DoesNotContain("echo stale", text, StringComparison.Ordinal);
        Assert.Equal(before, text[..text.IndexOf(begin, StringComparison.Ordinal)]);
        Assert.Equal(after, text[(text.LastIndexOf(end, StringComparison.Ordinal) + end.Length)..]);
    }

    [Theory]
    [InlineData("#!/usr/bin/env python3\nprint('hi')\n", HookTextOutcome.RefusedNotPosixShell)]
    [InlineData("echo no shebang\n", HookTextOutcome.RefusedNoShebang)]
    [InlineData("#!/bin/sh\r\necho crlf\r\n", HookTextOutcome.RefusedCrlf)]
    public void InstallRefusesANonPosixOrShebangLessOrCrlfHookAndLeavesItUnchanged(string existing, HookTextOutcome expected)
    {
        HookTextEdit edit = PostCheckoutHookText.Install(existing);

        Assert.Equal(expected, edit.Outcome);
        Assert.Equal(existing, edit.Text ?? existing);
    }

    [Fact]
    public void RemoveDropsOnlyTheBlockAndKeepsTheOriginalHook()
    {
        const string original = "#!/bin/bash\necho one\nexit 0\n";
        string installed = PostCheckoutHookText.Install(original).Text ?? string.Empty;

        HookTextEdit edit = PostCheckoutHookText.Remove(installed);

        Assert.Equal(HookTextOutcome.Removed, edit.Outcome);
        Assert.Equal(original, edit.Text);
    }

    [Fact]
    public void RemoveOfAHookHoldingOnlyTheShebangAndBlockAsksForTheFileToBeDeleted()
    {
        string installed = PostCheckoutHookText.Install(null).Text ?? string.Empty;

        HookTextEdit edit = PostCheckoutHookText.Remove(installed);

        Assert.Equal(HookTextOutcome.DeleteFile, edit.Outcome);
    }

    [Theory]
    [InlineData("command -v kyber-weave")]
    [InlineData("doctor --post-checkout")]
    [InlineData("${1:-}")]
    [InlineData("</dev/null")]
    [InlineData("2>&1 &")]
    [InlineData("GIT_DIR")]
    [InlineData("GIT_WORK_TREE")]
    [InlineData("GIT_INDEX_FILE")]
    [InlineData("GIT_PREFIX")]
    [InlineData("GIT_COMMON_DIR")]
    [InlineData("kyber-weave-index.log")]
    public void TheBlockCarriesTheGuardsTheHookDependsOn(string required)
    {
        Assert.Contains(required, PostCheckoutHookText.Block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The block is inserted after the shebang and must survive set -e and set -u. A bare exit would end the hook
    /// before the rest of the original body ran, so the block may never call it.
    /// </summary>
    [Fact]
    public void TheBlockNeverCallsExit()
    {
        Assert.DoesNotMatch(@"\bexit\b", PostCheckoutHookText.Block);
    }
}
