using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Core.CodeGraph;
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
/// <para>
/// D23 applies the refresh only where CodeGraph is declared, and <c>mcp.required-servers</c> is where
/// a repository declares it. Every test here that expects a refresh therefore arranges a repository
/// that requires CodeGraph (<see cref="ToolsRequiringCodeGraph"/>); the test that pins the other side
/// leaves the scaffold's own declaration — <c>kw-docs</c> alone, CodeGraph commented out — untouched.
/// </para>
/// </remarks>
public sealed class McpCodeGraphRefreshTests : IDisposable
{
    /// <summary>How a busy lock is reported: one line naming the index work already under way.</summary>
    private static readonly Regex BusyNote = new(
        @"(?im)^.*\b(?:codegraph|index)\b.*\b(?:busy|lock|refresh\w*|another)\b.*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// How a repository that set <c>stop-on-tool-failure: false</c> is told CodeGraph was unusable:
    /// one line opening with <c>warning:</c>.
    /// </summary>
    private static readonly Regex CodeGraphWarning = new(
        @"(?im)^warning:.*codegraph.*$",
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
        DocsTools tools = ToolsRequiringCodeGraph(Root);
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
        DocsTools tools = ToolsRequiringCodeGraph(Root);
        string lockPath = LockPath(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Stopwatch elapsed = Stopwatch.StartNew();
        string response = tools.Explore("anything");
        elapsed.Stop();

        TimeSpan waitBudget = TimeSpan.FromSeconds(2);
        Assert.True(
            elapsed.Elapsed < waitBudget,
            $"The tool call spent {elapsed.Elapsed.TotalSeconds:0.0}s with the refresh lock held by another "
            + $"process; a busy lock must not wait (tight budget {waitBudget.TotalSeconds:0}s).");
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
    /// When the refresh throws an exception (e.g. resolver construction failure), the failure is
    /// converted to STATUS: TOOL_FAILURE rather than escaping, and the throttle does not bypass the gate.
    /// </summary>
    [Fact]
    public void WhenResolverFactoryThrowsDuringRefresh_FailsToolsUnderStopRule()
    {
        DocsScaffolder.Scaffold(Root);
        McpDeclarationFixture.RequireCodeGraph(Root);

        int resolverCalls = 0;
        DocsTools tools = ScaffoldedTools(
            Root,
            () =>
            {
                resolverCalls++;
                throw new InvalidOperationException("boom resolver failed");
            });

        string first = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", first, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", first, StringComparison.Ordinal);
        Assert.DoesNotContain("documents=0", first, StringComparison.Ordinal);

        // Subsequent call must not answer Clear as if healthy
        string second = tools.Explore("anything");
        Assert.Contains("STATUS: TOOL_FAILURE", second, StringComparison.Ordinal);
        Assert.Contains("boom resolver failed", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only genuine sharing violations are classified as contention; non-lock IO exceptions
    /// (such as PathTooLongException or disk errors) are not treated as busy contention.
    /// </summary>
    [Fact]
    public void NonSharingViolationIOException_IsNotTreatedAsBusyLock()
    {
        Assert.False(CodeGraphProvisioner.IsSharingViolation(new PathTooLongException("path too long")));
        Assert.False(CodeGraphProvisioner.IsSharingViolation(new IOException("disk sector error", unchecked((int)0x80070017))));

        Assert.True(CodeGraphProvisioner.IsSharingViolation(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(CodeGraphProvisioner.IsSharingViolation(new IOException("The process cannot access the file because it is being used by another process.")));
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
        DocsTools tools = ToolsRequiringCodeGraph(Root);
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

    /// <summary>
    /// A repository whose declaration names no CodeGraph takes no index lock and gets ordinary docs
    /// answers, whichever way it set the stop rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D23 applies the refresh only where CodeGraph is declared, and <c>mcp.required-servers</c> is
    /// where a repository declares it. The configuration <c>docs init</c> scaffolds declares
    /// <c>kw-docs</c> and leaves the CodeGraph entry commented out, so the scaffold's own default is a
    /// repository that asked for no index. Refreshing there spends a provision on a dependency the
    /// repository never claimed, and once CodeGraph is not installed every docs question in that
    /// repository answers <c>STATUS: TOOL_FAILURE</c> — a failure about the tool's own guess rather
    /// than about anything the repository declared.
    /// </para>
    /// <para>
    /// The refresh is observed through the index lock, the same seam the tests above use: a refresh
    /// that ran would have taken it. The resolver is the production adapter over this repository,
    /// which has no index, so a refresh that ran would find CodeGraph unusable and report it in the
    /// form the stop rule selects — the failure under the default, the warning line under
    /// <c>stop-on-tool-failure: false</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARepositoryThatRequiresNoCodeGraphNeitherRefreshesNorFailsItsDocsTools(bool stopsOnToolFailure)
    {
        string stopRule = stopsOnToolFailure ? "the default stop rule" : "stop-on-tool-failure: false";
        string lockPath = LockPath(Root);
        RemoveStaleLock(lockPath);

        DocsScaffolder.Scaffold(Root);
        if (!stopsOnToolFailure)
        {
            File.AppendAllText(
                Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"),
                Environment.NewLine + "stop-on-tool-failure: false" + Environment.NewLine);
        }

        DocsTools tools = ScaffoldedTools(Root, () => CodeGraphResolverAdapter.ForRepository(Root));

        string response = tools.Explore("anything");

        Assert.False(
            File.Exists(lockPath),
            $"A repository that declares no CodeGraph took the index lock at '{lockPath}', so it refreshed an "
            + "index it never asked for.");
        Assert.DoesNotContain("STATUS: TOOL_FAILURE", response, StringComparison.Ordinal);
        Assert.False(
            CodeGraphWarning.IsMatch(response),
            $"Under {stopRule} the response carries a CodeGraph warning although the repository declares no "
            + $"CodeGraph. It was:{Environment.NewLine}{response}");
        Assert.StartsWith("provenance:", response, StringComparison.Ordinal);
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

    /// <summary>
    /// Tools over a repository that requires CodeGraph: the scaffold as it comes, plus the CodeGraph
    /// entry it leaves commented out. This is the repository the refresh applies to.
    /// </summary>
    private static DocsTools ToolsRequiringCodeGraph(string root)
    {
        DocsScaffolder.Scaffold(root);
        McpDeclarationFixture.RequireCodeGraph(root);
        return ScaffoldedTools(root, () => FakeCodeGraphResolver.WithSymbols());
    }

    /// <summary>Tools over the corpus already scaffolded at <paramref name="root"/>.</summary>
    /// <remarks>
    /// The refresh schedule is asked for explicitly. It is opt-in: tools built without one answer
    /// from the documents on disk, so a test that observes the refresh has to be the caller that
    /// requests it.
    /// </remarks>
    private static DocsTools ScaffoldedTools(string root, Func<ICodeGraphResolver> resolverFactory)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(root);
        DocumentIndexHost host = new(
            root,
            resolverFactory,
            () => new DocumentLoader(root, config.Ontology).Load(),
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);

        return new DocsTools(host, null, new CodeGraphFreshness(root, refreshEnabled: true));
    }
}
