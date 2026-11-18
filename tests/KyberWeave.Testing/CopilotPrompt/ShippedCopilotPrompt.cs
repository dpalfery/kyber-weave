using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Domain.CopilotPrompt;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>The real embedded-resource adapter over the assembly that ships the Copilot prompt, for tests that need the shipped text.</summary>
internal static class ShippedCopilotPrompt
{
    internal static IEmbeddedResources Resources { get; } = new AssemblyEmbeddedResources(typeof(CopilotPromptFile).Assembly);
}
