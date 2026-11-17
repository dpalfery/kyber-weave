using System.Runtime.CompilerServices;
using KyberWeave.Tests;

namespace KyberWeave.Integration.Tests;

/// <summary>Runs the shared test setup before this assembly's first test.</summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => TestRuntime.Initialize();
}
