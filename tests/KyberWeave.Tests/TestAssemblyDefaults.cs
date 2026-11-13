using System.Runtime.CompilerServices;
using KyberWeave.Cli.Commands.Docs;

namespace KyberWeave.Tests;

/// <summary>
/// Settings every test starts from. The task packet check asks a paid model when a key is configured, so a test run never
/// reaches one unless a test installs a provider for it.
/// </summary>
internal static class TestAssemblyDefaults
{
    [ModuleInitializer]
    internal static void DisableLivePacketCheck() => DocsTasksCommand.PacketProvider = _ => null;
}
