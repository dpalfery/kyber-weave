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
    /// A lock held past the best-effort bound must fail closed: the post cannot take the
    /// pre-dispatch snapshot, so the fact stays absent instead of falling back to a
    /// <c>HEAD</c> diff the dispatch's commits never appear in.
    /// </summary>
    [Fact]
    public void LockContentionLeavesSinceDispatchAbsent()
    {
        Pre(prompt: PlannerPrompt, callId: "call-locked");

        using (File.Open(LockPath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            ArbiterFactSet facts = PostFacts(prompt: PlannerPrompt, callId: "call-locked");

            Assert.False(
                facts.IsPresent(SinceDispatchFact),
                "a snapshot that cannot be taken under contention is unobservable and must stay absent.");
        }
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
