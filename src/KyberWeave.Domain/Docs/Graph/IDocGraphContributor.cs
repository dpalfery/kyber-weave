using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>Port for adding independently governed concepts to DocGraph.</summary>
public interface IDocGraphContributor
{
    /// <summary>Builds a contribution for the current immutable document snapshot.</summary>
    DocGraphContribution Contribute(DocumentSet documents, ICodeGraphResolver codeGraph);
}
