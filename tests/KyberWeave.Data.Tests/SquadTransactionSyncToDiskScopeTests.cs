using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the contract of the asynchronous-flow override <see cref="SquadTransaction.UseSyncToDiskScope" />, split out of
///     the render seam contract tests because the transaction lives in the data layer.
/// </summary>
public sealed class SquadTransactionSyncToDiskScopeTests
{
    /// <summary>
    ///     Verifies that SquadTransaction.UseSyncToDiskScope isolates disk sync overrides to the current
    ///     asynchronous flow so concurrent test threads keep the process-wide default.
    /// </summary>
    [Fact]
    public async Task UseSyncToDiskScopeIsolatesOverrideToCurrentAsyncFlow()
    {
        // The process-wide default is whatever the test host set; the scope must flip it to the opposite value in this flow only.
        bool baseline = SquadTransaction.SyncToDisk;
        bool overridden = !baseline;

        using (SquadTransaction.UseSyncToDiskScope(overridden))
        {
            Assert.Equal(overridden, SquadTransaction.SyncToDisk);
            await Task.Yield();
            Assert.Equal(overridden, SquadTransaction.SyncToDisk);

            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread;
            using (ExecutionContext.SuppressFlow())
            {
                thread = new Thread(static state =>
                {
                    TaskCompletionSource<bool> completionSource = (TaskCompletionSource<bool>)state!;
                    completionSource.SetResult(SquadTransaction.SyncToDisk);
                })
                {
                    IsBackground = true
                };
                thread.Start(tcs);
            }

            bool independentObserved = await tcs.Task;
            thread.Join();
            Assert.Equal(baseline, independentObserved);
        }

        Assert.Equal(baseline, SquadTransaction.SyncToDisk);
    }
}
