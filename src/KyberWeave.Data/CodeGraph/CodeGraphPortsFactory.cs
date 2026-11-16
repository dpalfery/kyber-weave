using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;

namespace KyberWeave.Data.CodeGraph;

/// <summary>Opens the CodeGraph ports over a checkout's index, one <c>sqlite3</c> read per call.</summary>
public sealed class CodeGraphPortsFactory : ICodeGraphPortsFactory
{
    /// <inheritdoc />
    public CodeGraphPorts ForRepository(string repositoryRoot)
    {
        CodeGraphResolverAdapter adapter = CodeGraphResolverAdapter.ForRepository(repositoryRoot);
        return new CodeGraphPorts(adapter, adapter, adapter, adapter);
    }
}
