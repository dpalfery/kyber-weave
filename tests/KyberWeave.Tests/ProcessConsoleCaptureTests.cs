using KyberWeave.Cli.Rendering;
using Spectre.Console;
using Xunit;

namespace KyberWeave.Tests;

public sealed class ProcessConsoleCaptureTests
{
    [Fact]
    public async Task ConcurrentWorkersExecuteInParallelWithoutCrossTalkOrWidthInterference()
    {
        const int workerCount = 16;
        using Barrier barrier = new(workerCount);
        Task<CapturedConsoleExecution<int>>[] tasks = new Task<CapturedConsoleExecution<int>>[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i;
            tasks[i] = Task.Factory.StartNew(() =>
            {
                return ProcessConsoleCapture.Run(() =>
                {
                    int assignedWidth = (workerId % 2 == 0) ? (1000 + workerId) : 80;
                    AnsiConsole.Profile.Width = assignedWidth;

                    // Barrier synchronization: if Run held a lock, workers would deadlock here.
                    bool synchronized = barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                    Assert.True(synchronized, $"Worker {workerId} timed out waiting at the concurrency barrier.");

                    // Verify width remains strictly isolated to this worker despite concurrent mutations.
                    Assert.Equal(assignedWidth, AnsiConsole.Profile.Width);

                    Console.WriteLine($"<<Console-Message-{workerId}>>");
                    AnsiConsole.MarkupLine($"<<Ansi-Message-[bold]{workerId}[/]>>");

                    // Brief yield to allow interleaved writes across concurrent tasks
                    Thread.Sleep(5);

                    Assert.Equal(assignedWidth, AnsiConsole.Profile.Width);
                    return workerId;
                });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        CapturedConsoleExecution<int>[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < workerCount; i++)
        {
            CapturedConsoleExecution<int> capture = results[i];
            Assert.Equal(i, capture.Result);
            Assert.Contains($"<<Console-Message-{i}>>", capture.Output, StringComparison.Ordinal);
            Assert.Contains($"<<Ansi-Message-{i}>>", capture.Output, StringComparison.Ordinal);

            // Verify absolute absence of cross-talk from any other worker
            for (int other = 0; other < workerCount; other++)
            {
                if (other == i)
                {
                    continue;
                }

                Assert.DoesNotContain($"<<Console-Message-{other}>>", capture.Output, StringComparison.Ordinal);
                Assert.DoesNotContain($"<<Ansi-Message-{other}>>", capture.Output, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void NestedRunsRestoreOuterCaptureContext()
    {
        CapturedConsoleExecution<string> outer = ProcessConsoleCapture.Run(() =>
        {
            Console.WriteLine("outer-before");
            AnsiConsole.MarkupLine("ansi-outer-before");

            CapturedConsoleExecution<int> inner = ProcessConsoleCapture.Run(() =>
            {
                Console.WriteLine("inner-message");
                AnsiConsole.MarkupLine("ansi-inner-message");
                return 42;
            });

            Assert.Equal(42, inner.Result);
            Assert.Contains("inner-message", inner.Output, StringComparison.Ordinal);
            Assert.Contains("ansi-inner-message", inner.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("outer-before", inner.Output, StringComparison.Ordinal);

            Console.WriteLine("outer-after");
            AnsiConsole.MarkupLine("ansi-outer-after");
            return "outer-done";
        });

        Assert.Equal("outer-done", outer.Result);
        Assert.Contains("outer-before", outer.Output, StringComparison.Ordinal);
        Assert.Contains("ansi-outer-before", outer.Output, StringComparison.Ordinal);
        Assert.Contains("outer-after", outer.Output, StringComparison.Ordinal);
        Assert.Contains("ansi-outer-after", outer.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("inner-message", outer.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ansi-inner-message", outer.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UncapturedExecutionRoutesToOriginalFallbackConsole()
    {
        // Outside Run, AnsiConsole and Console.Out route to fallback without throwing.
        Assert.NotNull(AnsiConsole.Console);
        Assert.NotNull(Console.Out);
        Assert.True(AnsiConsole.Profile.Width > 0);
    }
}
