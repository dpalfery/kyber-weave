using System.Runtime.ExceptionServices;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     Runs a transaction a test blocks mid-step, and waits for it to arrive at that step.
/// </summary>
/// <remarks>
///     Both halves exist because of the same failure: a thread pool the parallel test classes and their own blocking
///     waits have taken can leave a <see cref="Task.Run(Action)" /> transaction unreached for seconds, and a wait that
///     then expires reports only its own timeout, hiding whatever fault stopped the transaction from arriving.
/// </remarks>
internal static class BlockedTransaction
{
    /// <summary>
    ///     Starts a transaction on a thread of its own, so reaching its blocked step does not depend on the thread pool.
    /// </summary>
    /// <param name="execute">The transaction call.</param>
    /// <returns>The task the test awaits once it has released the observer.</returns>
    internal static Task Start(Action execute) =>
        Task.Factory.StartNew(
            execute,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>
    ///     Waits for the observer's blocked step and, when the transaction finished instead, rethrows its own
    ///     exception: a faulted transaction otherwise reports only the timeout, which says nothing about the cause.
    /// </summary>
    /// <param name="reached">The signal the observer sets at the blocked step.</param>
    /// <param name="execution">The transaction's task.</param>
    /// <param name="timeoutMessage">What a transaction that is still running has failed to do.</param>
    internal static void WaitForStep(ManualResetEventSlim reached, Task execution, string timeoutMessage)
    {
        // The budget only has to outlast a loaded CI runner starting one thread. The work before the blocked step is
        // a handful of small directory and file writes, so a transaction that has not arrived by then is stuck.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!reached.IsSet && !execution.IsCompleted && DateTime.UtcNow < deadline)
            Thread.Sleep(TimeSpan.FromMilliseconds(10));

        if (reached.IsSet)
            return;

        if (execution.IsFaulted)
            ExceptionDispatchInfo.Capture(execution.Exception!.GetBaseException()).Throw();

        Assert.Fail(timeoutMessage);
    }
}
