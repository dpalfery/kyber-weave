using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The pre/post snapshot ledger in <c>HookGitFacts</c> must fail closed: every path that
/// loses the pre-dispatch snapshot (no snapshot, consumed snapshot, torn file, held lock,
/// wrong pairing) leaves <c>git.changed-paths.since-dispatch</c> absent so the planner
/// rule answers <c>undecidable</c> (<c>escalate</c>) instead of mistaking an unobservable
/// dispatch for an empty diff (<c>inside</c>/<c>allow</c>).
/// </summary>
/// <remarks>
/// Every test runs through the real path — a real repository from
/// <see cref="RunStartProbe" />, the suite's one allow-listed driver of a real git
/// repository, the real classifier and fact builder, and the real
/// <c>HookGitFacts.Enrich</c> — because a hand-fed fact set proved nothing in the
/// earlier planner-diff rounds.
/// </remarks>
public sealed class HookGitFactsSnapshotTests : IDisposable
{
    private const string SinceDispatchFact = "git.changed-paths.since-dispatch";
    private const string PlannerPrompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nPlan prompt";

    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _arbiter = new();

    public void Dispose()
    {
        _probe.Dispose();
        _arbiter.Dispose();
    }

    /// <summary>
    /// No pre-dispatch snapshot plus a committed dispatch write must stay absent: the old
    /// fallback diffed against current <c>HEAD</c> with empty maps, saw a clean tree, and
    /// answered <c>inside</c>/<c>allow</c> for a write it never observed.
    /// </summary>
    [Fact]
    public void MissingPreSnapshotLeavesSinceDispatchAbsent()
    {
        Write("docs/plans/dispatch.md", "// committed dispatch write\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "feat: dispatch write");
        Write("docs/plans/dirty.md", "// uncommitted dispatch write\n");

        ArbiterFactSet facts = PostFacts(prompt: PlannerPrompt, callId: "call-missing");

        Assert.False(
            facts.IsPresent(SinceDispatchFact),
            "without a pre-dispatch snapshot the dispatch delta is unobservable and must stay absent (fail closed).");
    }

    /// <summary>
    /// A retry carrying the same tool-call id must see the same baseline: the old take
    /// removed the entry, so the second evaluation fell back to <c>HEAD</c> and flipped
    /// <c>outside</c> to <c>inside</c>.
    /// </summary>
    [Fact]
    public void RetryWithSameToolCallIdReusesTheSameBaseline()
    {
        const string callId = "call-retry-1";
        Pre(prompt: PlannerPrompt, callId: callId);

        Write("src/Dispatch.cs", "// dispatch write\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "feat: dispatch write");

        IReadOnlyList<string> first = SinceDispatch(prompt: PlannerPrompt, callId: callId);
        IReadOnlyList<string> second = SinceDispatch(prompt: PlannerPrompt, callId: callId);

        Assert.Contains("src/Dispatch.cs", first);
        Assert.Contains("src/Dispatch.cs", second);
    }

    /// <summary>
    /// A post with no tool-call id and an empty prompt must not pair by target alone: the
    /// factory harness never sends a tool-call id, so target-only pairing hands a dispatch
    /// another dispatch's baseline. A post repeating the recorded prompt still pairs.
    /// </summary>
    [Fact]
    public void EmptyPromptDoesNotPairByTargetAlone()
    {
        const string recorded = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/other.md\n\nOther plan";
        Pre(prompt: recorded, callId: null);

        ArbiterFactSet unpaired = PostFacts(prompt: string.Empty, callId: null);

        Assert.False(
            unpaired.IsPresent(SinceDispatchFact),
            "an empty prompt with no tool-call id carries no pairing key and must stay absent.");

        ArbiterFactSet paired = PostFacts(prompt: recorded, callId: null);

        Assert.True(
            paired.IsPresent(SinceDispatchFact),
            "a post repeating the recorded prompt still pairs with its pre-dispatch snapshot.");
    }

    /// <summary>
    /// The full host-supplied prompt must never reach <c>snapshots.json</c> verbatim: the
    /// match key is a hash of the prompt, and pairing still works through the real path.
    /// </summary>
    [Fact]
    public void SnapshotsFileNeverCarriesTheVerbatimPrompt()
    {
        string marker = "SECRET-PROMPT-MARKER-" + Guid.NewGuid().ToString("N");
        string prompt = $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/{marker}.md\n\n{marker}";
        Pre(prompt: prompt, callId: null);

        string store = File.ReadAllText(SnapshotsPath(), Encoding.UTF8);

        Assert.DoesNotContain(marker, store, StringComparison.Ordinal);

        ArbiterFactSet paired = PostFacts(prompt: prompt, callId: null);

        Assert.True(
            paired.IsPresent(SinceDispatchFact),
            "hashing the prompt must not break pairing through the real path.");
    }

    /// <summary>
    /// A pre-dispatch record lost to lock contention must fail closed: with no baseline the
    /// post cannot isolate the dispatch delta, so the fact stays absent.
    /// </summary>
    [Fact]
    public void RecordLostUnderContentionLeavesSinceDispatchAbsent()
    {
        using (File.Open(LockPath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Pre(prompt: PlannerPrompt, callId: "call-locked");
        }

        ArbiterFactSet facts = PostFacts(prompt: PlannerPrompt, callId: "call-locked");

        Assert.False(
            facts.IsPresent(SinceDispatchFact),
            "a snapshot that cannot be recorded under contention is unobservable and must stay absent.");
    }

    /// <summary>
    /// The post-dispatch read never waits on the lock: pairing a recorded baseline is a
    /// read of a whole document, so a held lock must not turn a good baseline absent.
    /// </summary>
    [Fact]
    public void PeekPairsWithoutTheLock()
    {
        const string callId = "call-peek-locked";
        Pre(prompt: PlannerPrompt, callId: callId);

        Write("src/Peeked.cs", "// dispatch write\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "feat: dispatch write");

        using (File.Open(LockPath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            IReadOnlyList<string> paths = SinceDispatch(prompt: PlannerPrompt, callId: callId);
            Assert.Contains("src/Peeked.cs", paths);
        }
    }

    /// <summary>
    /// Genuine absence resolves promptly: unpaired posts never wait out a retry budget
    /// for a baseline no record can produce.
    /// </summary>
    [Fact]
    public void GenuineAbsenceResolvesPromptly()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            ArbiterFactSet facts = PostFacts(prompt: PlannerPrompt, callId: $"call-never-recorded-{i}");
            Assert.False(facts.IsPresent(SinceDispatchFact), "an unrecorded dispatch must stay absent.");
        }

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(15),
            $"five absent posts took {elapsed.Elapsed}, the absence path must stay fast.");
    }

    /// <summary>
    /// A pre with no pairing key stores nothing: no tool-call id and no prompt can never
    /// match a post, so keeping the entry only evicts baselines that could still pair.
    /// </summary>
    [Fact]
    public void UnpairablePreStoresNothing()
    {
        Pre(prompt: string.Empty, callId: null);

        Assert.False(
            File.Exists(SnapshotsPath()),
            "an entry no post can pair with must not occupy the store.");

        ArbiterFactSet facts = PostFacts(prompt: string.Empty, callId: null);

        Assert.False(
            facts.IsPresent(SinceDispatchFact),
            "without a pairing key the dispatch delta is unobservable and must stay absent.");
    }

    /// <summary>
    /// Baselines expire: a new pre prunes entries older than the retention window, so
    /// completed dispatches cannot pin the store full and evict a pending baseline.
    /// </summary>
    [Fact]
    public void ExpiredBaselinesArePrunedOnRecord()
    {
        const string staleHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        File.WriteAllText(
            SnapshotsPath(),
            "[{\"CallId\":\"call-stale\",\"Target\":\"architect\",\"PromptHash\":\"" + staleHash + "\"," +
            "\"Head\":\"abc123\",\"BlobIds\":{},\"Statuses\":{},\"RecordedAt\":\"2020-01-01T00:00:00Z\"}]",
            Encoding.UTF8);

        Pre(prompt: PlannerPrompt, callId: "call-fresh");

        string store = File.ReadAllText(SnapshotsPath(), Encoding.UTF8);
        Assert.DoesNotContain(staleHash, store, StringComparison.Ordinal);

        ArbiterFactSet paired = PostFacts(prompt: PlannerPrompt, callId: "call-fresh");
        Assert.True(
            paired.IsPresent(SinceDispatchFact),
            "pruning stale entries must not disturb the fresh baseline.");
    }

    /// <summary>
    /// A crashed write leaves no debris behind: the next record sweeps stray temp files.
    /// </summary>
    [Fact]
    public void StrayTempFilesAreCleanedOnRecord()
    {
        File.WriteAllText(
            Path.Combine(_arbiter.Path, ".snapshots.crashed.tmp"),
            "{\"truncated",
            Encoding.UTF8);

        Pre(prompt: PlannerPrompt, callId: "call-sweep");

        Assert.Empty(Directory.GetFiles(_arbiter.Path, ".snapshots.*.tmp"));
        Assert.True(
            PostFacts(prompt: PlannerPrompt, callId: "call-sweep").IsPresent(SinceDispatchFact),
            "sweeping debris must not disturb the fresh baseline.");
    }

    /// <summary>
    /// The match key hashes every prompt byte: two prompts sharing a prefix and a length
    /// but not a tail still key apart, so a dispatch never pairs with another's baseline.
    /// </summary>
    [Fact]
    public void LongPromptsPairByBoundedHash()
    {
        string head = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/big.md\n\n";
        string big = head + new string('p', 100_000);
        string bigOtherLength = big + "extra";
        string bigOtherTail = head + new string('p', 99_999) + "q";
        Pre(prompt: big, callId: null);

        Assert.True(
            PostFacts(prompt: big, callId: null).IsPresent(SinceDispatchFact),
            "a long prompt must still pair with itself.");
        Assert.False(
            PostFacts(prompt: bigOtherLength, callId: null).IsPresent(SinceDispatchFact),
            "a prompt sharing only a prefix must not pair with another dispatch's baseline.");
        Assert.False(
            PostFacts(prompt: bigOtherTail, callId: null).IsPresent(SinceDispatchFact),
            "a prompt sharing a prefix and a length but not a tail must not pair.");
    }

    /// <summary>
    /// A burst of dispatches must not evict a pending baseline: seventy pres go in, the
    /// first one's post still pairs instead of resolving absent.
    /// </summary>
    [Fact]
    public void BurstOfPreservesPendingBaselines()
    {
        const int burst = 70;
        for (int i = 0; i < burst; i++)
        {
            Pre(prompt: PlannerPrompt + $"\n# burst {i}", callId: $"call-burst-{i}");
        }

        IReadOnlyList<string> paths = SinceDispatch(prompt: PlannerPrompt + "\n# burst 0", callId: "call-burst-0");
        Assert.NotNull(paths);
    }

    /// <summary>
    /// The store must be a whole JSON document with no debris: a torn write previously
    /// truncated <c>snapshots.json</c>, the reader returned <c>[]</c>, and every pending
    /// snapshot fell back to <c>HEAD</c>. A corrupt file still degrades to absent, never
    /// to a guessed diff.
    /// </summary>
    [Fact]
    public void SnapshotStoreIsWholeJsonAndCorruptionFailsClosed()
    {
        Pre(prompt: PlannerPrompt, callId: "call-atomic");

        using (JsonDocument.Parse(File.ReadAllText(SnapshotsPath(), Encoding.UTF8)))
        {
        }

        string[] debris = Directory.GetFiles(_arbiter.Path)
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "snapshots.json", StringComparison.Ordinal)
                && !string.Equals(name, ".snapshots.lock", StringComparison.Ordinal))
            .ToArray()!;
        Assert.Empty(debris);

        File.WriteAllText(SnapshotsPath(), "{\"truncated", Encoding.UTF8);
        ArbiterFactSet facts = PostFacts(prompt: PlannerPrompt, callId: "call-atomic");

        Assert.False(
            facts.IsPresent(SinceDispatchFact),
            "a corrupt snapshot store carries no trustworthy baseline and must stay absent.");
    }

    private void Pre(string prompt, string? callId)
    {
        ArbiterEvent pre = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "architect",
            Prompt = prompt,
            ToolCallId = callId,
            HarnessCaller = "conductor",
            Cwd = _probe.Root,
            IsDispatch = true,
        };
        TriggerClassification classification = TriggerClassifier.ClassifySingle(pre, "architect", prompt);
        Assert.Equal("delegate.planner", classification.Trigger);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, KyberWeaveConfig.ProductDefaults);
        new HookGitFacts(_probe.Root, null, _arbiter.Path).Enrich(facts, classification, pre);
    }

    private ArbiterFactSet PostFacts(string prompt, string? callId)
    {
        ArbiterEvent post = new()
        {
            Harness = "factory",
            Phase = "post",
            Target = "architect",
            Prompt = prompt,
            ToolCallId = callId,
            HarnessCaller = "conductor",
            Cwd = _probe.Root,
            IsDispatch = true,
        };
        TriggerClassification classification = TriggerClassifier.ClassifySingle(post, "architect", prompt);
        Assert.Equal("delegate.planner.returned", classification.Trigger);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, KyberWeaveConfig.ProductDefaults);
        return new HookGitFacts(_probe.Root, null, _arbiter.Path).Enrich(facts, classification, post);
    }

    private IReadOnlyList<string> SinceDispatch(string prompt, string? callId)
    {
        ArbiterFactSet facts = PostFacts(prompt, callId);
        Assert.True(
            facts.TryGet(SinceDispatchFact, out ArbiterFact? fact) && fact!.Value is not null,
            "a recorded pre-dispatch snapshot must supply git.changed-paths.since-dispatch.");
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(fact!.Value);
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private string SnapshotsPath() => Path.Combine(_arbiter.Path, "snapshots.json");

    private string LockPath() => Path.Combine(_arbiter.Path, ".snapshots.lock");
}
