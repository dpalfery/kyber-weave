using System.Runtime.CompilerServices;

namespace KyberWeave.Tests;

/// <summary>Runs the shared test setup before this assembly's first test.</summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => TestRuntime.Initialize();
}
