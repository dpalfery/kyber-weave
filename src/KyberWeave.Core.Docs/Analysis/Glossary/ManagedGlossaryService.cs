using KyberWeave.Core.Configuration;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Core.Docs.Analysis.Glossary;

/// <summary>
/// The managed glossary over the physical file system. Forwards to <see cref="ManagedGlossary"/> until the callers that
/// still build it themselves take <c>IManagedGlossaryFactory</c> from the container.
/// </summary>
/// <param name="repositoryRoot">The repository root.</param>
/// <param name="config">The effective configuration.</param>
/// <param name="timeProvider">The clock that dates new rows.</param>
public sealed class ManagedGlossaryService(string repositoryRoot, KyberWeaveConfig config, TimeProvider timeProvider)
    : ManagedGlossary(repositoryRoot, config, timeProvider, new PhysicalRepositoryFileStore());
