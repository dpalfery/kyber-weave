using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests.Squad;

/// <summary>The instance link sync crash-safety contract, stopped at every file operation of the in-memory file system.</summary>
public sealed class InMemorySquadInstanceLinkSyncCrashSafetyTests : SquadInstanceLinkSyncCrashSafetyContractTests
{
    /// <inheritdoc />
    protected override IReadOnlyList<SquadLinkSyncKnownWeakness> KnownWeaknesses =>
    [
        new(
            SquadLinkSyncFlow.FirstSync,
            "'state/links.json' changed",
            [.. Enumerable.Range(17, 12)],
            SquadLinkSyncKnownWeaknesses.UnrecordedLinksReason),
        new(
            SquadLinkSyncFlow.UnmanagedCopyKept,
            "'state/links.json' changed",
            [.. Enumerable.Range(17, 7)],
            SquadLinkSyncKnownWeaknesses.UnrecordedLinksReason),
    ];

    /// <inheritdoc />
    protected override SquadLinkSyncCrashWorkspace CreateWorkspace() => new InMemorySquadLinkSyncCrashWorkspace();
}
