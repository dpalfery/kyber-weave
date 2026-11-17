using KyberWeave.Cli.Commands.Docs;

namespace KyberWeave.Tests;

/// <summary>
/// Settings every test starts from. The task packet check asks a paid model when a key is configured, so a test run never
/// reaches one unless a test installs a provider for it. The agent pick is a registered service: <c>DocsTestCommands.Services</c> registers none.
/// </summary>
internal static class TestAssemblyDefaults
{
    internal static void DisableLivePacketCheck()
    {
        DocsTasksCommand.PacketProviderOverride = _ => null;
    }
}
