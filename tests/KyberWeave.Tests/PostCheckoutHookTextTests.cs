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

    [Fact]
    public void AHookThatOnlyMentionsTheMarkerTextKeepsEveryLineOfItsOwn()
    {
        const string begin = "# >>> kyber-weave worktree index >>>";
        const string end = "# <<< kyber-weave worktree index <<<";
        string existing = $"#!/bin/sh\necho \"{begin}\"\nkeep-me\necho \"{end}\"\n";

        HookTextEdit edit = PostCheckoutHookText.Install(existing);

        Assert.Equal(HookTextOutcome.Inserted, edit.Outcome);
        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + existing["#!/bin/sh\n".Length..], edit.Text);
    }

    [Fact]
    public void InstallOverTwoCopiesOfTheBlockLeavesOne()
    {
        string existing = "#!/bin/sh\n" + PostCheckoutHookText.Block + "own-line\n" + PostCheckoutHookText.Block;

        HookTextEdit edit = PostCheckoutHookText.Install(existing);

        Assert.Equal(HookTextOutcome.Updated, edit.Outcome);
        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + "own-line\n", edit.Text);
    }

    [Fact]
    public void AStrayEndMarkerIsClearedSoALaterInstallIsUnchanged()
    {
        const string end = "# <<< kyber-weave worktree index <<<";
        string existing = $"#!/bin/sh\nown-line\n{end}\n";

        HookTextEdit first = PostCheckoutHookText.Install(existing);
        HookTextEdit second = PostCheckoutHookText.Install(first.Text!);

        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + "own-line\n", first.Text);
        Assert.Equal(HookTextOutcome.Unchanged, second.Outcome);
    }

    [Theory]
    [InlineData("#!/usr/bin/env FOO=bar bash\necho hi\n")]
    [InlineData("#!/bin/sh.exe\necho hi\n")]
    [InlineData("#!/usr/bin/env -u FOO bash\necho hi\n")]
    [InlineData("#!/usr/bin/env -a name bash\necho hi\n")]
    [InlineData("#!/usr/bin/env -iu FOO bash\necho hi\n")]
    [InlineData("#!/usr/bin/env -S bash -e\necho hi\n")]
    public void InstallAcceptsAShellNamedThroughEnvAssignmentsOrWithAnExeSuffix(string existing)
    {
        Assert.Equal(HookTextOutcome.Inserted, PostCheckoutHookText.Install(existing).Outcome);
    }

    [Fact]
    public void AStrayBeginMarkerGoesWithTheBlockLinesAfterItAndTheOwnersLinesStay()
    {
        string[] block = PostCheckoutHookText.Block.Split('\n');
        string truncated = string.Join('\n', block.Take(4)) + "\n";
        string existing = "#!/bin/sh\n" + truncated + "own-line\n";

        HookTextEdit installed = PostCheckoutHookText.Install(existing);
        HookTextEdit removed = PostCheckoutHookText.Remove(existing);

        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + "own-line\n", installed.Text);
        Assert.Equal(HookTextOutcome.Unchanged, PostCheckoutHookText.Install(installed.Text!).Outcome);
        Assert.Equal("#!/bin/sh\nown-line\n", removed.Text);
    }

    [Fact]
    public void AnIndentedLineThatReadsLikeAMarkerIsTheOwnersAndStays()
    {
        const string begin = "# >>> kyber-weave worktree index >>>";
        const string end = "# <<< kyber-weave worktree index <<<";
        string existing = $"#!/bin/sh\ncat <<EOF\n  {begin}\n  keep-me\n  {end}\nEOF\n";

        HookTextEdit installed = PostCheckoutHookText.Install(existing);

        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + existing["#!/bin/sh\n".Length..], installed.Text);
        Assert.Equal(HookTextOutcome.Unchanged, PostCheckoutHookText.Remove(existing).Outcome);
    }

    [Fact]
    public void MarkersWithTrailingWhitespaceAreStillTheBlock()
    {
        string padded = PostCheckoutHookText.Block
            .Replace(">>>\n", ">>>  \n", StringComparison.Ordinal)
            .Replace("<<<\n", "<<<\t\n", StringComparison.Ordinal);

        HookTextEdit edit = PostCheckoutHookText.Install("#!/bin/sh\n" + padded + "own-line\n");

        Assert.Equal(HookTextOutcome.Updated, edit.Outcome);
        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block + "own-line\n", edit.Text);
    }

    [Fact]
    public void InstallOnAnEmptyHookWritesItLikeAMissingOne()
    {
        HookTextEdit edit = PostCheckoutHookText.Install(string.Empty);

        Assert.Equal(HookTextOutcome.Created, edit.Outcome);
        Assert.Equal("#!/bin/sh\n" + PostCheckoutHookText.Block, edit.Text);
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
