using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.1: the run stores (<c>ArbiterRunStore</c>). RED: the store does not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// The stores hold the text of review findings and worker failures outside the decision log and
/// the ledger, so a rule can read them back without the decision log ever carrying their text.
/// Items are appended through <see cref="ArbiterLogFiles.AppendLineAsync(string, string, string, System.Threading.CancellationToken)"/>,
/// the same locked append the ledger uses.
/// </para>
/// <para>
/// Assumed names (GREEN must match): <c>ArbiterRunStore(string arbiterDirectory, ArbiterStoreOptions? options,
/// Func&lt;DateTimeOffset&gt;? clock)</c>, <c>ArbiterStoreOptions(int MaxItemBytes, int RetainDays)</c> with
/// defaults 16384 and 14, and <c>ArbiterStoreStatus { Stored, TooLarge, Unavailable }</c>.
/// </para>
/// </remarks>
public sealed class ArbiterRunStoreTests : IDisposable
{
    private const string SentinelSecret = "KW-TEST-SENTINEL-SECRET-do-not-store-4d1e";

    private static readonly string[] NoRuleIds = ["KW-ARB-SCOPE-002"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-stores-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private ArbiterRunStore Store(DateTimeOffset at, ArbiterStoreOptions? options = null) =>
        new(ArbiterDirectory, options, () => at);

    private static ArbiterLedgerEvent PreEvent(DateTimeOffset at) =>
        new(ArbiterRecordId.New(at), at, ArbiterSources.Hook, "test-harness", "session-1", ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
            PairDigest = Sha256Hex("prompt-body"),
        };

    private static ArbiterDecisionRecord Decision(DateTimeOffset at, string ledgerId) =>
        new(
            ArbiterRecordId.New(at),
            at,
            ArbiterSources.Hook,
            "test-harness",
            "session-1",
            ledgerId,
            "delegate",
            "conductor",
            ArbiterCallerSources.Harness,
            "csharp-dev",
            "docs/plans/plan.md",
            "plan-digest-1",
            "3.1",
            "ruleset-sha",
            [new ArbiterDecisionRule(
                NoRuleIds[0], 0, "beyond-files", null, null, null, RuleEffects.Escalate, [], "one line of evidence")],
            new ArbiterProviderRecord(
                "rules", null, null, null, null, ArbiterProviderStatuses.ShortCircuited),
            RuleEffects.Escalate,
            1,
            42);

    // ---- Locations (acceptance: findings and failures land in their keyed directories) ----

    /// <summary>
    /// The review store is keyed by the <c>REVIEW_RUN</c> id's digest, so two runs never share a file
    /// and a finding is found by the run it belongs to.
    /// </summary>
    [Fact]
    public async Task FindingsLandInTheReviewRunDirectoryKeyedByDigest()
    {
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "finding one");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        string expected = Path.Combine(
            ArbiterDirectory, "stores", "review-" + Sha256Hex("RUN-A"), "findings.jsonl");
        Assert.True(File.Exists(expected), $"No findings file at '{expected}'.");
        Assert.Contains("finding one", await File.ReadAllTextAsync(expected), StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure is keyed by the plan file and its digest, so a failure stays with the plan it came from.
    /// </summary>
    [Fact]
    public async Task FailuresLandInTheRunDirectoryKeyedByPlanFileAndDigest()
    {
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFailureAsync(
            "docs/plans/plan.md", "plan-digest-1", "session-1", "worker failed on 3.1");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        string expected = Path.Combine(
            ArbiterDirectory,
            "stores",
            "run-" + Sha256Hex("docs/plans/plan.md" + "plan-digest-1"),
            "failures.jsonl");
        Assert.True(File.Exists(expected), $"No failures file at '{expected}'.");
        Assert.Contains("worker failed on 3.1", await File.ReadAllTextAsync(expected), StringComparison.Ordinal);
    }

    /// <summary>
    /// With no plan to key on, a failure is keyed by its session, so an ad-hoc dispatch still gets a store.
    /// </summary>
    [Fact]
    public async Task FailuresWithoutAPlanAreKeyedBySession()
    {
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFailureAsync(
            planFile: null, planDigest: null, session: "session-9", text: "ad-hoc failure");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        string expected = Path.Combine(
            ArbiterDirectory, "stores", "run-" + Sha256Hex("session-9"), "failures.jsonl");
        Assert.True(File.Exists(expected), $"No failures file at '{expected}'.");
    }

    /// <summary>Reading a run returns exactly what was stored for that run, and nothing from another run.</summary>
    [Fact]
    public async Task ReadingAReviewRunReturnsOnlyItsOwnFindings()
    {
        ArbiterRunStore store = Store(Now);
        await store.AppendFindingAsync("RUN-A", "finding of run A");
        await store.AppendFindingAsync("RUN-B", "finding of run B");

        IReadOnlyList<string> runA = store.ReadFindings("RUN-A");

        Assert.Equal(["finding of run A"], runA);
    }

    // ---- Size limit (acceptance: oversize is refused, never shortened) ----

    /// <summary>
    /// An item over <c>arbiter.stores.max-item-bytes</c> (16384 by default) is not stored, and reading
    /// the run reports it missing. The text is never shortened to make it fit.
    /// </summary>
    [Fact]
    public async Task AnItemOverTheDefaultByteLimitIsNotStoredAndReadsAsMissing()
    {
        string oversize = new('x', 16385);
        Assert.Equal(16385, Encoding.UTF8.GetByteCount(oversize));
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-BIG", oversize);

        Assert.Equal(ArbiterStoreStatus.TooLarge, status);
        IReadOnlyList<string> read = store.ReadFindings("RUN-BIG");
        Assert.Empty(read);
        Assert.DoesNotContain(read, text => text.Contains("xxxx", StringComparison.Ordinal));
    }

    /// <summary>
    /// The limit is inclusive: an item of exactly 16384 bytes is stored in full, and reading it back gives
    /// the same text, unshortened.
    /// </summary>
    [Fact]
    public async Task AnItemExactlyAtTheDefaultByteLimitIsStoredUnshortened()
    {
        string atLimit = new('y', 16384);
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-EDGE", atLimit);

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        IReadOnlyList<string> read = store.ReadFindings("RUN-EDGE");
        Assert.Single(read);
        Assert.Equal(atLimit, read[0]);
    }

    /// <summary>The configured limit replaces the default, so a lower cap refuses a smaller item.</summary>
    [Fact]
    public async Task TheConfiguredByteLimitReplacesTheDefault()
    {
        ArbiterRunStore store = Store(Now, new ArbiterStoreOptions(MaxItemBytes: 10, RetainDays: 14));

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-SMALL", new string('z', 11));

        Assert.Equal(ArbiterStoreStatus.TooLarge, status);
    }

    // ---- Retention (acceptance: old store directories are deleted on the next append) ----

    /// <summary>
    /// A store directory older than <c>stores.retain-days</c> (14 days) is deleted on the next append. The
    /// clock is anchored to real time and advanced 15 days, so the test holds whether the directory's age is
    /// read from its file time or from the time recorded in it.
    /// </summary>
    [Fact]
    public async Task AStoreDirectoryOlderThanTheRetentionWindowIsDeletedOnTheNextAppend()
    {
        DateTimeOffset anchor = Now;
        await Store(anchor).AppendFindingAsync("RUN-OLD", "written long ago");
        string oldDirectory = Path.Combine(ArbiterDirectory, "stores", "review-" + Sha256Hex("RUN-OLD"));
        Assert.True(Directory.Exists(oldDirectory));

        ArbiterRunStore later = Store(anchor.AddDays(15));
        await later.AppendFindingAsync("RUN-NEW", "written today");

        Assert.False(Directory.Exists(oldDirectory), $"'{oldDirectory}' is past the retention window and was kept.");
        Assert.True(Directory.Exists(Path.Combine(ArbiterDirectory, "stores", "review-" + Sha256Hex("RUN-NEW"))));
    }

    /// <summary>
    /// A store directory still inside the retention window (13 days) is kept by the same append.
    /// </summary>
    [Fact]
    public async Task AStoreDirectoryInsideTheRetentionWindowIsKept()
    {
        DateTimeOffset anchor = Now;
        await Store(anchor).AppendFindingAsync("RUN-RECENT", "written recently");
        string recentDirectory = Path.Combine(ArbiterDirectory, "stores", "review-" + Sha256Hex("RUN-RECENT"));

        await Store(anchor.AddDays(13)).AppendFindingAsync("RUN-LATER", "written later");

        Assert.True(Directory.Exists(recentDirectory), $"'{recentDirectory}' is inside the window and was deleted.");
    }

    // ---- Unavailable store (acceptance: never throws) ----

    /// <summary>
    /// A store that cannot be created reports unavailable and never throws. A regular file where the
    /// <c>stores</c> directory must go is the unwritable case, and it fails on every platform without
    /// relying on permissions.
    /// </summary>
    [Fact]
    public async Task AnUnwritableStoreReportsUnavailableAndDoesNotThrow()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        await File.WriteAllTextAsync(Path.Combine(ArbiterDirectory, "stores"), "not a directory");
        ArbiterRunStore store = Store(Now);

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-X", "cannot be kept");

        Assert.Equal(ArbiterStoreStatus.Unavailable, status);
        Assert.Empty(store.ReadFindings("RUN-X"));
    }

    // ---- No secrets in the decision log or the ledger (acceptance: sentinel) ----

    /// <summary>
    /// The finding's text is kept in its store and never copied into the decision log or the ledger. The
    /// store is asserted to hold the sentinel and both other files are asserted to exist and be non-empty,
    /// so the absence check cannot pass on files that were never written.
    /// </summary>
    [Fact]
    public async Task ASentinelSecretInAStoredFindingNeverReachesTheDecisionLogOrTheLedger()
    {
        ArbiterRunStore store = Store(Now);
        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-SECRET", $"finding text carrying {SentinelSecret}");
        Assert.Equal(ArbiterStoreStatus.Stored, status);

        InFlightLedger ledger = new(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        ArbiterLedgerEvent pre = PreEvent(Now);
        await ledger.AppendAsync(pre);
        await decisions.AppendAsync(Decision(Now, pre.Id));

        string storedText = await File.ReadAllTextAsync(
            Path.Combine(ArbiterDirectory, "stores", "review-" + Sha256Hex("RUN-SECRET"), "findings.jsonl"));
        Assert.Contains(SentinelSecret, storedText, StringComparison.Ordinal);

        Assert.True(File.Exists(ledger.FilePath), "The ledger was never written, so the absence check proves nothing.");
        Assert.True(File.Exists(decisions.FilePath), "The decision log was never written, so the absence check proves nothing.");
        string ledgerText = await File.ReadAllTextAsync(ledger.FilePath);
        string decisionsText = await File.ReadAllTextAsync(decisions.FilePath);
        Assert.NotEmpty(ledgerText);
        Assert.NotEmpty(decisionsText);
        Assert.DoesNotContain(SentinelSecret, ledgerText, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelSecret, decisionsText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store directory's own time does not move when a file inside it is appended to, so retention must read the
    /// time of the items, not the directory. A run appended to recently survives the sweep even when its directory is
    /// old. Failure this guards: a review active for more than the retention window loses its findings mid-run,
    /// because only the directory's time is read.
    /// </summary>
    [Fact]
    public async Task ARunAppendedToRecentlyIsNotSweptWhenItsDirectoryIsOld()
    {
        ArbiterRunStore store = new(ArbiterDirectory, null, () => Now);
        await store.AppendFindingAsync("RUN-OLD", "id: old-1\nclaim: first");
        string runDirectory = Assert.Single(Directory.GetDirectories(Path.Combine(ArbiterDirectory, "stores")));
        Directory.SetLastWriteTimeUtc(runDirectory, Now.UtcDateTime.AddDays(-30));

        await store.AppendFindingAsync("RUN-OLD", "id: old-2\nclaim: recent");
        await store.AppendFindingAsync("RUN-NEW", "id: new-1\nclaim: fresh");

        Assert.Contains(store.ReadFindings("RUN-OLD"), item => item.Contains("id: old-2", StringComparison.Ordinal));
    }
}
