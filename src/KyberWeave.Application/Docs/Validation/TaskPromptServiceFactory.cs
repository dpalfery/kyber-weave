using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Builds the one task prompt service for a checkout, over the documentation that checkout holds right now.
/// </summary>
/// <param name="tasks">Reads and checks the task file the prompt comes from.</param>
/// <param name="hostFacts">Builds the host facts part, with the task's gates resolved by technology.</param>
/// <param name="files">Reads the prior-results file a retry names.</param>
/// <param name="codeGraph">Opens the CodeGraph ports over the checkout, so the standards lookup can resolve code joins.</param>
public sealed class TaskPromptServiceFactory(
    IPlanTaskService tasks,
    IPacketHostFacts hostFacts,
    IFileSystem files,
    ICodeGraphPortsFactory codeGraph) : ITaskPromptServiceFactory
{
    /// <inheritdoc />
    public ITaskPromptService ForRepository(string repositoryRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = Path.GetFullPath(repositoryRoot);
        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(root, config.Ontology).Load(),
            codeGraph.ForRepository(root).Resolver);
        return new TaskPromptService(tasks, hostFacts, files, index.StandardsFor);
    }
}
