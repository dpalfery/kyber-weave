using System.Runtime.CompilerServices;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests;

internal static class SquadTestDurability
{
    [ModuleInitializer]
    internal static void DisableDeviceFlush() => SquadTransaction.SyncToDisk = false;
}
