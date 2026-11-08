using KyberWeave.Cli.Commands.Run;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The resume log read in <c>kyber-weave run start</c>: a git log the step cannot read is a
/// warning on a resume that can still continue, while a programming error stays loud.
/// </summary>
/// <remarks>
/// These cases call the read itself rather than driving the CLI as a child process, because
/// the distinction under test — which exceptions become a warning — is unreachable through
/// the command's own guards: the refs it passes are always non-empty, so the argument
/// guards on <c>RunStartRunner.CommitMessages</c> never fire there. A future caller passing
/// an empty ref must hear about it instead of resuming with a warning.
/// </remarks>
public sealed class RunStartResumeLogTests
{
    /// <summary>
    /// A programming error while reading the resume log escapes instead of becoming a
    /// warning: an <c>ArgumentException</c> like the guards on
    /// <c>RunStartRunner.CommitMessages</c> throw is a defect, not a log that cannot be read.
    /// </summary>
    [Fact]
    public void AProgrammingErrorWhileReadingTheResumeLogEscapes()
    {
        ArgumentException failure = new("tip");

        Assert.Throws<ArgumentException>(() =>
            RunStartCommand.TryReadResumeLog(
                () => throw failure,
                "origin/claude/fix/tighten-the-gate",
                out _,
                out _));
    }

    /// <summary>
    /// A cancelled wait while reading the resume log escapes instead of becoming a warning:
    /// cancellation is the caller's signal, not a log that cannot be read.
    /// </summary>
    [Fact]
    public void ACancelledWaitWhileReadingTheResumeLogEscapes()
    {
        Assert.Throws<OperationCanceledException>(() =>
            RunStartCommand.TryReadResumeLog(
                () => throw new OperationCanceledException(),
                "origin/claude/fix/tighten-the-gate",
                out _,
                out _));
    }

    /// <summary>
    /// A missing log reader is a programming error, not an unreadable log: a null delegate
    /// throws instead of passing as a soft warning.
    /// </summary>
    [Fact]
    public void AMissingLogReaderWhileReadingTheResumeLogThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RunStartCommand.TryReadResumeLog(
                null!,
                "origin/claude/fix/tighten-the-gate",
                out _,
                out _));
    }

    /// <summary>
    /// A git log that exits non-zero warns and continues with no finished tasks: a branch
    /// carrying none is a normal beginning, not a failure.
    /// </summary>
    [Fact]
    public void AGitLogExitFailureWhileReadingTheResumeLogWarnsAndContinues()
    {
        RunStartCommand.TryReadResumeLog(
            () => new GitOutcome(1, string.Empty, "fatal: bad revision"),
            "origin/claude/fix/tighten-the-gate",
            out IReadOnlyList<RunStartCompletedTask> completed,
            out string? completedWarning);

        Assert.Empty(completed);
        Assert.True(
            completedWarning is not null &&
            completedWarning.Contains("origin/claude/fix/tighten-the-gate", StringComparison.Ordinal),
            $"a failed git log must warn naming the ref, not '{completedWarning}'.");
    }

    /// <summary>
    /// A git log child that cannot start still warns and continues: a process error is a log
    /// that cannot be read, which the earlier exit-contract repair already established.
    /// </summary>
    [Fact]
    public void AGitLogProcessErrorWhileReadingTheResumeLogWarnsAndContinues()
    {
        RunStartCommand.TryReadResumeLog(
            () => throw new IOException("Permission denied"),
            "origin/claude/fix/tighten-the-gate",
            out IReadOnlyList<RunStartCompletedTask> completed,
            out string? completedWarning);

        Assert.Empty(completed);
        Assert.True(
            completedWarning is not null &&
            completedWarning.Contains("origin/claude/fix/tighten-the-gate", StringComparison.Ordinal),
            $"an unreadable git log must warn naming the ref, not '{completedWarning}'.");
    }

    /// <summary>A git log that reads cleanly reports its tasks with no warning.</summary>
    [Fact]
    public void ASuccessfulResumeLogReadReportsNoWarning()
    {
        RunStartCommand.TryReadResumeLog(
            () => new GitOutcome(0, string.Empty, string.Empty),
            "origin/claude/fix/tighten-the-gate",
            out IReadOnlyList<RunStartCompletedTask> completed,
            out string? completedWarning);

        Assert.Empty(completed);
        Assert.Null(completedWarning);
    }
}
