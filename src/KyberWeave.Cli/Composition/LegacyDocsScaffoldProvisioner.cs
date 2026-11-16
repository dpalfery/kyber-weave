using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Cli.Composition;

/// <summary>
/// Creates the project documentation scaffold through the legacy scaffolder.
/// </summary>
/// <remarks>
/// The scaffolder is still in <c>KyberWeave.Core.Docs</c> until the Docs CLI leftovers move; this adapter keeps the
/// install service free of that reference, and repoints at the moved type when it lands.
/// </remarks>
internal sealed class LegacyDocsScaffoldProvisioner : IDocsScaffoldProvisioner
{
    /// <inheritdoc />
    public void EnsureProjectScaffold(string root) => DocsScaffolder.Scaffold(root);
}
