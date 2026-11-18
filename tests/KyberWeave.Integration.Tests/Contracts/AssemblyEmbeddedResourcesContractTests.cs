using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Tests.EmbeddedResources;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the embedded-resource contract against the real adapter, which reads the assembly's manifest resources.</summary>
public sealed class AssemblyEmbeddedResourcesContractTests : EmbeddedResourceContractTests
{
    private const string PromptResource = "CopilotPrompt.system-prompt.yaml";
    private const string PromptSourcePath = "products/kyber-squad/copilot/system-prompt.yaml";

    /// <inheritdoc />
    protected override string KnownResource => PromptResource;

    /// <inheritdoc />
    protected override string KnownResourceText =>
        File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, PromptSourcePath));

    /// <inheritdoc />
    protected override string MissingResource => "CopilotPrompt.no-such-resource.yaml";

    /// <inheritdoc />
    protected override IEmbeddedResources CreateResources() =>
        new AssemblyEmbeddedResources(typeof(CopilotPromptFile).Assembly);
}
