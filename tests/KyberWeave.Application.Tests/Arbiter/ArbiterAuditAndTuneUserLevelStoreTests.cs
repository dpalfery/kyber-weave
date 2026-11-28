using System.Security.Cryptography;
using System.Text;
using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// <c>arbiter audit</c> and <c>arbiter tune</c> read the store the user-level hook records in. A repository under a
/// user-level install keeps its ledger under the home directory rather than under <c>artifacts/arbiter</c>, and a
/// report of nothing from an empty project store was the defect. The whole flow runs over the in-memory host, with git
/// scripted so no process starts.
/// </summary>
public sealed class ArbiterAuditAndTuneUserLevelStoreTests : IDisposable
{
    private const string LensRule = "KW-ARB-LENS-001";

    private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create(ScriptGit);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AuditAsync_ReportsTheDispatchesTheUserLevelHookRecorded()
    {
        string root = Repository();
        string store = UserLevelStoreHolding(root, InFlightLedger.FileName);
        await _host.State.Ledger(store).AppendAsync(new ArbiterLedgerEvent(
            "0000000000000001-aaaaaaaa", At, "hook", "claude", "session-1", ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
        });

        ArbiterAuditReport? report = await _host.Command<ArbiterAuditService>().AuditAsync(
            root, KyberWeaveConfigDefaults.Product, null, null, null, new DiagnosticReport());

        Assert.NotNull(report);
        Assert.Equal(1, report.EventCount);
    }

    [Fact]
    public async Task TuneAsync_ScoresTheDecisionsTheUserLevelHookRecorded()
    {
        string root = Repository();
        string store = UserLevelStoreHolding(root, DecisionLog.FileName);
        await _host.State.Decisions(store).AppendAsync(ScoredDecision());

        ArbiterTuneReport? report = await _host.Command<ArbiterTuneService>().ScoreAsync(root, [0.6], null, new DiagnosticReport());

        Assert.NotNull(report);
        ArbiterTuneRule rule = Assert.Single(report.Rules);
        Assert.Equal(LensRule, rule.Id);
        Assert.Equal(1, rule.Scored);
    }

    /// <summary>The repository the commands report on: a directory of its own under the host's working directory.</summary>
    private string Repository()
    {
        string root = _host.InWork("host");
        _host.Files.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// The store the user-level hook records under for <paramref name="root" />, holding <paramref name="fileName" />:
    /// the layout and the key are spelled out here rather than taken from the code under test. The real append leaves
    /// the file on disk, which is what the locator looks for; the fake log keeps its lines in memory only, so the file
    /// is written beside them.
    /// </summary>
    private string UserLevelStoreHolding(string root, string fileName)
    {
        string store = Path.Combine(
            _host.HomeDirectory, ".kyber-weave", "arbiter", "projects",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root))));
        _ = _host.Write(Path.Combine(store, fileName), string.Empty);
        return store;
    }

    /// <summary>One decision carrying a single step-1 answer at confidence 0.45, the shape <c>arbiter tune</c> scores.</summary>
    private static ArbiterDecisionRecord ScoredDecision() =>
        new(
            "d-1",
            At,
            "hook",
            "claude",
            null,
            "ledger-1",
            "lens.spawn",
            "code-reviewer",
            "header",
            "review-lens",
            null,
            null,
            null,
            "shipped",
            [new ArbiterDecisionRule(LensRule, 1, "within-task", null, 0.45, 0.6, "allow", [], null) { Shadow = true }],
            new ArbiterProviderRecord("systemone", null, "jev-1.13.0", null, 12, ArbiterProviderStatuses.Answered),
            "allow",
            0,
            5);

    /// <summary>
    /// A git that treats the directory it runs in as a main checkout: that directory is the working tree's root and its
    /// <c>.git</c> the repository's common git directory.
    /// </summary>
    private static void ScriptGit(IServiceCollection services) =>
        services.AddSingleton<IGitCommandRunner>(new FakeGitCommandRunner((directory, arguments) =>
            new GitOutcome(
                0,
                (arguments.Contains("--git-common-dir", StringComparer.Ordinal) ? Path.Combine(directory, ".git") : directory)
                + Environment.NewLine,
                string.Empty)));
}
