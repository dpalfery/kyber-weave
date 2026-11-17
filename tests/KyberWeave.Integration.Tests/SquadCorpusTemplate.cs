using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The checked-in Squad corpus, loaded once for the test class that declares this fixture.
/// </summary>
/// <remarks>
/// Loading the corpus is the expensive part of a test that only reads it: parsing the product's
/// manifest, bundles, agents, skills and profiles costs about 75 ms, and several tests in this
/// class loaded it twice — once through the lifecycle's render and once for the assertions, plus
/// once per target in the render guard. What a test reads from it is immutable, so one class-wide
/// load serves them all without sharing anything a test could mutate. The release archive every
/// test still extracts for itself is <see cref="Fakes.CorpusSquadReleaseSource"/>, which copies it
/// into the test's own directory; that copy costs less than this parse and stays per-test because
/// it is the directory the lifecycle downloads into.
/// </remarks>
public sealed class SquadCorpusTemplate
{
    /// <summary>Loads the checked-in corpus once for the class that declares this fixture.</summary>
    public SquadCorpusTemplate()
    {
        Source = SquadSourceLoader.Load(Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad"));
    }

    /// <summary>The corpus, parsed once and never mutated.</summary>
    internal SquadSource Source { get; }
}
