using KyberWeave.Tests.Corpus;

namespace KyberWeave.Testing.Squad;

/// <summary>The global receipt the shipped 0.1.8-rc.20 binary wrote, compiled into this assembly so a unit test reads it from memory.</summary>
internal static class SquadRc20Fixtures
{
    /// <summary>Reads the two-block global receipt: rc.20's own bytes, assembled per the recipe in the test that pins it.</summary>
    internal static string TwoBlocksReceipt() =>
        EmbeddedFixtureText.Read(typeof(SquadRc20Fixtures).Assembly, "squad-rc20-global/two-blocks.receipt.json");
}
