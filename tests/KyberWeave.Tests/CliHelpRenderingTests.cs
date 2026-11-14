using System.Diagnostics;
using System.Xml.Linq;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every command and branch of the real CLI must render its <c>--help</c>. Spectre.Console parses an option
/// description as markup, so an unescaped <c>[0, 1]</c> in one makes the help fail after the usage line.
/// </summary>
public sealed class CliHelpRenderingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const int TailLength = 160;

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void TheFailureTailOfShortOutputIsAllOfIt(string output)
    {
        Assert.Equal(output, Tail(output));
    }

    [Fact]
    public void TheFailureTailOfOutputWithManyWindowsLineEndingsDoesNotThrowAndKeepsTheEnd()
    {
        string output = string.Concat(Enumerable.Repeat("line\r\n", 500)) + "the end";

        string tail = Tail(output);

        Assert.Equal(160, tail.Length);
        Assert.EndsWith("the end", tail, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', tail);
    }

    [Fact]
    public void EveryCommandAndBranchRendersItsHelp()
    {
        string[][] paths = [.. CommandPaths()];
        Assert.True(paths.Length > 50, "the command model listed too few commands to be the whole tree");

        List<string> failures = [];
        foreach (string[] path in paths)
        {
            ProcessResult result = RunCli([.. path, "--help"]);
            string output = result.StandardOutput + "\n" + result.StandardError;
            if (result.ExitCode != 0
                || output.Contains("Could not find color or style", StringComparison.Ordinal)
                || output.Contains("Error:", StringComparison.Ordinal))
            {
                failures.Add($"{string.Join(' ', path)} (exit {result.ExitCode}): {Tail(output)}");
            }
        }

        Assert.True(failures.Count == 0, "help failed to render for:\n" + string.Join('\n', failures));
    }

    /// <summary>The last 160 characters of the output on one line, cut from the same string that is shown.</summary>
    private static string Tail(string output)
    {
        string oneLine = output.Trim().ReplaceLineEndings(" ");
        return oneLine.Length <= TailLength ? oneLine : oneLine[^TailLength..];
    }

    private static IEnumerable<string[]> CommandPaths()
    {
        ProcessResult model = RunCli(["cli", "xmldoc"]);
        Assert.Equal(0, model.ExitCode);
        XElement root = XDocument.Parse(model.StandardOutput).Root!;
        return Walk(root, []);

        static IEnumerable<string[]> Walk(XElement parent, string[] prefix)
        {
            foreach (XElement command in parent.Elements("Command"))
            {
                string[] path = [.. prefix, (string)command.Attribute("Name")!];
                yield return path;
                foreach (string[] inner in Walk(command, path))
                    yield return inner;
            }
        }
    }

    private static ProcessResult RunCli(string[] args)
    {
        string host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "kyber-weave.exe" : "kyber-weave");
        Assert.True(File.Exists(host), $"The kyber-weave host '{host}' is missing from the test output directory.");
        ProcessStartInfo startInfo = new(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);

        return ProcessRunner.Run(startInfo, string.Empty, Timeout);
    }
}
