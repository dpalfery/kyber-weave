namespace KyberWeave.Tests;

/// <summary>
/// The process-wide settings every test run needs, called from a module initializer in each
/// test assembly rather than from one here.
/// </summary>
/// <remarks>
/// A module initializer in this library would run when the library is first touched, which can
/// be after the test platform has already read its pipe path and after the first deployment has
/// started. Each test assembly calls this before any test runs, so the temp root, the durability
/// switch and the packet-check default are in place in every suite that references this library.
/// </remarks>
internal static class TestRuntime
{
    /// <summary>Redirects the temp root, disables device flushes, and turns off the paid packet check.</summary>
    internal static void Initialize()
    {
        TestTempRoot.RedirectTemp();
        SquadTestDurability.DisableDeviceFlush();
        TestAssemblyDefaults.DisableLivePacketCheck();
    }
}
