using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
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
/// <param name="documents">Opens the documentation corpus the standards lookup indexes.</param>
/// <param name="paths">Resolves the repository root to a full path.</param>
/// <param name="environment">Names the directory a relative repository root is resolved against.</param>
/// <param name="skills">Builds the KW skill catalog the prompt's <c>kw-skills</c> part lists from, for the same root.</param>
public sealed class TaskPromptServiceFactory(
    IPlanTaskService tasks,
    IPacketHostFacts hostFacts,
    IWritableFileSystem files,
    ICodeGraphPortsFactory codeGraph,
    IDocumentSourceFactory documents,
    IPathResolver paths,
    IProcessEnvironment environment,
    IKwSkillCatalogFactory skills) : ITaskPromptServiceFactory
{
    /// <inheritdoc />
    public ITaskPromptService ForRepository(string repositoryRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        DocumentIndex index = DocumentIndex.Build(
            documents.Create(root, config.Ontology).Load(),
            codeGraph.ForRepository(root).Resolver);
        return new TaskPromptService(
            tasks,
            hostFacts,
            files,
            index.StandardsFor,
            environment,
            skills.ForRepository(root, config));
    }

    /// <inheritdoc />
    public async Task<ITaskPromptService> ForRepositoryAsync(
        string repositoryRoot,
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        DocumentSet loaded = documents.Create(root, config.Ontology).Load();
        CodeGraphPorts codeGraphPorts = await codeGraph.ForRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
        DocumentIndex index = DocumentIndex.Build(loaded, codeGraphPorts.Resolver);
        return new TaskPromptService(
            tasks,
            hostFacts,
            files,
            index.StandardsFor,
            environment,
            skills.ForRepository(root, config));
    }
}
