using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.CodeGraph;

namespace KyberWeave.Core.CodeGraph;

/// <summary>The one place the hosts that have no container build the CodeGraph ports.</summary>
/// <remarks>
/// The adapter reads the index through <c>sqlite3</c> and so lives in <c>KyberWeave.Data</c>, which entry apps never
/// reference. Core does reference Data, so it is the lowest project both the adapter and those hosts can see. A
/// caller that holds the container takes <see cref="ICodeGraphPortsFactory"/> from it instead; this wire stays for the
/// MCP host, the docs command composition and the review commands until they do.
/// </remarks>
public static class CodeGraphResolvers
{
    private static readonly ICodeGraphPortsFactory Factory = new CodeGraphPortsFactory();

    /// <summary>Builds the ports over the index under <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The checkout whose <c>.codegraph/codegraph.db</c> is read.</param>
    /// <returns>One object holding every port the loaded adapter answers.</returns>
    public static CodeGraphPorts ForRepository(string repositoryRoot) => Factory.ForRepository(repositoryRoot);
}
