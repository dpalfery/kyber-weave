using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests;

/// <summary>
/// The Squad deployment crash-safety contract, run against the in-memory file system: a process killed at every
/// mutating file-system operation of install, update and uninstall, then recovered, and every handled failure rolled
/// back. The same cases run on the real disk in the integration project.
/// </summary>
public sealed class InMemorySquadCrashSafetyTests : SquadCrashSafetyContractTests
{
    /// <summary>
    /// Stop points that leave recovery refusing the journal today. Each entry is a symptom, matched at any point of the
    /// flow because the sweep reaches it at many operations; an entry goes when its fix lands.
    /// </summary>
    protected override IReadOnlyList<SquadCrashKnownWeakness> KnownWeaknesses =>
    [
        .. new[] { SquadCrashFlow.Install, SquadCrashFlow.Update, SquadCrashFlow.Uninstall }.SelectMany(flow => new[]
        {
            Weakness(flow, "no authoritative intent journal", "a kill before the first journal write leaves an empty transaction folder that recovery refuses"),
            Weakness(flow, "unpublished temporary artifact", "a kill inside an atomic write leaves a temporary file that recovery refuses"),
            Weakness(flow, "authority does not exactly describe its transaction tree", "a kill leaves a transaction folder the journal does not describe, so recovery refuses"),
            Weakness(flow, "is missing or has the wrong node kind", "a kill during cleanup removes backups before the journal, so recovery refuses the journal"),
            Weakness(flow, "is neither the whole old tree", "a kill leaves a tree that is neither the old nor the new one")
        })
    ];

    /// <inheritdoc />
    protected override SquadCrashSafetyFixture CreateFixture() => new InMemorySquadCrashSafetyFixture();

    private static SquadCrashKnownWeakness Weakness(SquadCrashFlow flow, string symptom, string reason) =>
        new(flow, SquadCrashKnownWeakness.AnyPoint, symptom, reason);
}
