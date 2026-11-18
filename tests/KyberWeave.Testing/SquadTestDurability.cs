using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests;

internal static class SquadTestDurability
{
    internal static void DisableDeviceFlush() => SquadTransaction.SyncToDisk = false;
}
