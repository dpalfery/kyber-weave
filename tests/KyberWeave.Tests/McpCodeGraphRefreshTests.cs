using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Doctor;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the cadence of the docs server's own CodeGraph refresh (D23): the first tool call refreshes,
/// later calls do not, and a refresh another process is already running is reported as one note
/// rather than as a failure.
/// </summary>
/// <remarks>
/// <para>
/// The server used to leave the index to whoever happened to run <c>codegraph</c> last, so joins
/// answered from whatever was on disk when the session started. Refreshing on every call would put
/// an eight-second index behind every question. Refreshing on the first call and then at a bounded
/// interval is the compromise, and these tests pin the two ends that are observable from outside:
/// the first call does refresh, and the presence of another process's lock never turns a question
/// into an error.
/// </para>
/// <para>
/// The refresh is observed through the cross-process lock file the repository already uses for index
/// work (<see cref="CodeGraphProvisioner.LockPathFor"/>). That path is public and derived from the
/// repository root, so a temp root's lock is private to one test and no concurrent run can collide
/// with it. The lock is acquired with no sharing, which is what the provisioner does, so a busy
/// lock is the real condition rather than a simulated one.
/// </para>
/// </remarks>
public sealed class McpCodeGraphRefreshTests : IDisposable
{
    /// <summary>How a busy lock is reported: one line naming the index work already under way.</summary>
    private static readonly Regex BusyNote = new(
        @"(?im)^.*\b(?:codegraph|index)\b.*\b(?:busy|lock|refresh\w*|another)\b.*$",
        RegexOptions.CultureInvariant);

    /// <summary>The documented bound on one refresh, which a tool call may never exceed on top of its own work.</summary>
    private static readonly TimeSpan RefreshBound = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Root => _root.Path;

    /// <summary>
    /// The first tool call refreshes CodeGraph, which is why the index lock is taken during
    /// <c>Explore</c>.
    /// </summary>
    /// <remarks>
    /// D23: the first tool call refreshes. Without it a long session answers from the index as it
    /// stood when the server started, and the joins silently describe a tree that has since moved.
    /// The lock is the durable trace of that work: it is the same file the provisioner takes, so a
    /// refresh that skipped the lock would let two processes rewrite one index at once.
    /// </remarks>
    [Fact]
    public void TheFirstToolCallRefreshesCodeGraph()
    {
        DocsTools tools = Tools(Root);
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        _ = tools.Explore("anything");

        Assert.True(
            File.Exists(lockPath),
            $"The first docs tool call took no CodeGraph index lock at '{lockPath}', so it did not refresh the index.");
    }

    /// <summary>
    /// While another process holds the refresh lock the tool answers with a one-line note, does not
    /// report a tool failure, and returns inside the refresh bound.
    /// </summary>
    /// <remarks>
    /// D23: a busy lock means another process is already refreshing, so the answer on disk is at
    /// worst one refresh behind — it is not a failure, and it must not become a wait. Treating it as
    /// a failure would stop a run because two agents asked at the same moment; waiting on it would
    /// put another process's eight-second index on this call's critical path.
    /// </remarks>
    [Fact]
    public void ABusyRefreshLockAnswersOneNoteInsteadOfFailingOrWaiting()
    {
        DocsTools tools = Tools(Root);
        string lockPath = LockPath(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Stopwatch elapsed = Stopwatch.StartNew();
        string response = tools.Explore("anything");
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < RefreshBound,
            $"The tool call spent {elapsed.Elapsed.TotalSeconds:0.0}s with the refresh lock held by another "
            + $"process; the refresh may wait at most {RefreshBound.TotalSeconds:0}s.");
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.True(
            BusyNote.IsMatch(response),
            $"With another process holding the refresh lock the response carries no note naming the index "
            + $"work already under way. It was:{Environment.NewLine}{response}");
    }

    /// <summary>
    /// The interval suppresses a second refresh, so a burst of questions costs one index rather than
    /// one per question.
    /// </summary>
    /// <remarks>
    /// D23: at most once every 60 seconds. The proof here is that a second call made while the lock
    /// is held produces no busy note: a refresh attempt would have hit the held lock and said so, so
    /// its silence is the record that no attempt was made.
    /// </remarks>
    [Fact]
    public void TheRefreshIntervalSuppressesASecondAttempt()
    {
        DocsTools tools = Tools(Root);
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        _ = tools.Explore("anything");
        Assert.True(
            File.Exists(lockPath),
            $"The first docs tool call took no CodeGraph index lock at '{lockPath}', so it did not refresh the index.");
        DateTime firstRefresh = File.GetLastWriteTimeUtc(lockPath);

        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string second = tools.Explore("anything");

        Assert.True(
            !BusyNote.IsMatch(second),
            $"The second tool call inside the refresh interval attempted a refresh of its own. It answered:"
            + $"{Environment.NewLine}{second}");
        Assert.Equal(firstRefresh, File.GetLastWriteTimeUtc(lockPath));
    }

    private static string LockPath(string root) => CodeGraphProvisioner.LockPathFor(root, Path.GetTempPath());

    /// <summary>
    /// Clears a lock file a previous run of this machine left for this repository root. The root is a
    /// fresh temp directory, so the path names no repository any other process is working in.
    /// </summary>
    private static void RemoveStaleLock(string lockPath)
    {
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    private static DocsTools Tools(string root)
    {
        DocsScaffolder.Scaffold(root);
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentIndexHost host = new(
            root,
            () => FakeCodeGraphResolver.WithSymbols(),
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        return new DocsTools(host);
    }
}
