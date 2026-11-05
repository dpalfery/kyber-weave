using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Serializes the tests that swap <c>ArbiterCommandComposition</c>'s process-wide delegates
/// (home, credential store, key readers). Run in parallel, one class's constructor would clobber
/// another's fake collaborator mid-test, sending a write or delete to the previous class's fake.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definitions are named <Name>Collection by convention; "
        + "the suffix is the framework idiom, not a claim to be a collection type.")]
public sealed class ArbiterCommandCompositionCollection
{
    public const string Name = "ArbiterCommandComposition";
}
