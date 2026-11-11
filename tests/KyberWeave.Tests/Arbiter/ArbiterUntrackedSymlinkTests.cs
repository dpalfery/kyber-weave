using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// An untracked symbolic link is measured as a link, never as the file it points to. A link whose target is
/// a comment-only file must not make the change look comment-only, so the change set is absent (undecidable)
/// rather than measured. Ordinary untracked files are still measured.
/// </summary>
public sealed class ArbiterUntrackedSymlinkTests : IDisposable
{
    private readonly RunStartProbe _probe = RunStartProbe.Create();

    /// <summary>Deletes the probe repository.</summary>
    public void Dispose() => _probe.Dispose();

    /// <summary>
    /// An untracked link to a comment-only file is not read through: the change set is absent, so no lens
    /// can skip the link as a comment-only change.
    /// </summary>
    [Fact]
    public void AnUntrackedLinkToACommentOnlyFileLeavesTheChangeSetAbsent()
    {
        CommitFile("src/Tracked.cs", "class Tracked { }\n");
        string baseRef = Head();
        Write("notes/Comment.cs", "// only a comment\n");
        CreateLinkOrSkip("src/Link.cs", Path.Combine("..", "notes", "Comment.cs"));

        ChangeSet? change = GitFacts.GetChangeSet(_probe.Root, baseRef);

        Assert.Null(change);
    }

    /// <summary>
    /// Without the link the same comment-only file is an ordinary untracked file and is measured, so the
    /// fail-safe above applies to links alone.
    /// </summary>
    [Fact]
    public void AnUntrackedRegularFileIsStillMeasured()
    {
        CommitFile("src/Tracked.cs", "class Tracked { }\n");
        string baseRef = Head();
        Write("notes/Comment.cs", "// only a comment\n");

        ChangeSet? change = GitFacts.GetChangeSet(_probe.Root, baseRef);

        Assert.NotNull(change);
        Assert.Contains(change.Hunks, measured => measured.Hunk.Path == "notes/Comment.cs");
    }

    private void CreateLinkOrSkip(string relative, string target)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"this platform cannot create a symbolic link here: {ex.Message}");
        }
    }

    private string Head() =>
        GitFacts.GetHead(_probe.Root) ?? throw new InvalidOperationException("the probe repository has no HEAD.");

    private void CommitFile(string relative, string text)
    {
        Write(relative, text);
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", $"test: {relative}");
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }
}
