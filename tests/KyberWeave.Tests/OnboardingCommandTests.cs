using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>kyber-weave docs onboarding &lt;done|declined&gt;</c> is how a repository records that it has answered the
/// onboarding step, and how it records a refusal.
/// </summary>
/// <remarks>
/// <para>
/// The value lives in <c>.kyber-weave/kyber-weave.yml</c> and decides whether <c>run start</c> stops to offer the
/// onboarding skill. It is written through the host configuration rather than by hand because a hand edit loses
/// the operator's comments and leaves the Config Reg block in <c>AGENTS.md</c> describing the old file.
/// </para>
/// <para>
/// The cases drive the real CLI, through <see cref="RunStartProbe"/>, rather than a test-built
/// <c>CommandApp</c>: the claim being pinned is that the product registers the verb, and a composition assembled
/// in a test would prove only that the test can register it.
/// </para>
/// </remarks>
public sealed class OnboardingCommandTests : IDisposable
{
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";

    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary><c>docs onboarding done</c> writes the value and exits zero.</summary>
    /// <remarks>
    /// A repository that has onboarded must be able to say so, or every run after it stops again to ask the same
    /// question. The exit code matters as much as the line: the conductor dispatches this verb and branches on
    /// whether it succeeded.
    /// </remarks>
    [Fact]
    public void DocsOnboardingDoneRecordsTheValueAndExitsZero()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\n");

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done");

        Assert.True(result.ExitCode == 0, $"docs onboarding done exited {result.ExitCode}. {result.Output}");
        Assert.Contains("onboarding: done", ReadConfig().Split('\n').Select(l => l.TrimEnd('\r')), StringComparer.Ordinal);
    }

    [Fact]
    public void DocsOnboardingCanRepairInvalidOnboardingValue()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\nonboarding: dnoe\n");

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done");

        Assert.True(result.ExitCode == 0, $"docs onboarding done exited {result.ExitCode}. {result.Output}");
        Assert.Contains("onboarding: done", ReadConfig().Split('\n').Select(l => l.TrimEnd('\r')), StringComparer.Ordinal);
    }

    [Fact]
    public void DocsOnboardingNonExistentDirectoryRefusesWithExitTwo()
    {
        ProbeResult result = _probe.RunCli("docs", "onboarding", "done", Path.Combine(_probe.Root, "non-existent-directory"));

        Assert.True(result.ExitCode == 2, $"Expected exit code 2, got {result.ExitCode}. {result.Output}");
    }

    [Fact]
    public void DocsOnboardingRefusesSymlinkedConfig()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\n");
        string configPath = Path.Combine(_probe.Root, ConfigRelativePath);
        string realPath = Path.Combine(_probe.Root, "real-config.yml");
        File.Move(configPath, realPath);
        File.CreateSymbolicLink(configPath, realPath);

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done");

        Assert.True(result.ExitCode == 1, $"Expected exit code 1 for symlink, got {result.ExitCode}. {result.Output}");
        Assert.Contains("symbolic link", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>docs onboarding declined</c> records the refusal, which behaves as a completed onboarding and is never
    /// asked again.
    /// </summary>
    /// <remarks>
    /// A user who says no has to be able to stop the question. Without this value the only recorded answers are
    /// "done" and "never asked", so declining would leave the run asking on every start.
    /// </remarks>
    [Fact]
    public void DocsOnboardingDeclinedRecordsTheRefusalAndExitsZero()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\n");

        ProbeResult result = _probe.RunCli("docs", "onboarding", "declined");

        Assert.True(result.ExitCode == 0, $"docs onboarding declined exited {result.ExitCode}. {result.Output}");
        Assert.Contains("onboarding: declined", SettingLines(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Writing the setting keeps every comment and every other key the operator wrote.
    /// </summary>
    /// <remarks>
    /// The host configuration is a hand-authored file with comments explaining why each choice was made; a
    /// round trip through a YAML emitter discards them. The value is added by editing the text in place, and this
    /// case pins that the file a repository gets back is its own with one line changed.
    /// </remarks>
    [Fact]
    public void WritingTheSettingKeepsTheOperatorsCommentsAndKeys()
    {
        string original = """
            # Ours, not the tool's.
            ontology:
              docs-root: docs
              technologies:
                - csharp
            autonomy: autonomous
            """;
        _probe.WriteHostConfig(original + "\n");

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done");

        Assert.True(result.ExitCode == 0, $"docs onboarding done exited {result.ExitCode}. {result.Output}");
        string written = ReadConfig();
        Assert.Contains("# Ours, not the tool's.", written, StringComparison.Ordinal);
        Assert.Contains("autonomy: autonomous", written, StringComparison.Ordinal);
        Assert.Contains("    - csharp", written, StringComparison.Ordinal);
        Assert.Contains("onboarding: done", SettingLines(), StringComparer.Ordinal);
    }

    /// <summary>
    /// A value that is not one of the two is refused, the refusal names both, and the file is unchanged.
    /// </summary>
    /// <remarks>
    /// Anything else would record a state the run-start check cannot read, and a repository that believed it had
    /// onboarded would be asked again on the next start with no way to see why.
    /// </remarks>
    [Fact]
    public void AValueOutsideTheTwoIsRefusedAndNothingIsWritten()
    {
        string original = "ontology:\n  docs-root: docs\n";
        _probe.WriteHostConfig(original);

        ProbeResult result = _probe.RunCli("docs", "onboarding", "maybe");

        Assert.True(result.ExitCode != 0, $"docs onboarding maybe exited 0. {result.Output}");
        Assert.Contains("done", result.Output, StringComparison.Ordinal);
        Assert.Contains("declined", result.Output, StringComparison.Ordinal);
        Assert.Equal(original, ReadConfig());
    }

    /// <summary>The <c>docs</c> branch help lists the verb, so an operator can find it.</summary>
    /// <remarks>
    /// The verb is only reachable if the product registers it on the branch it belongs to. A command defined but
    /// never added to the composition compiles, and does nothing else.
    /// </remarks>
    [Fact]
    public void TheDocsBranchHelpListsOnboarding()
    {
        ProbeResult result = _probe.RunCli("docs", "--help");

        Assert.True(result.ExitCode == 0, $"docs --help exited {result.ExitCode}. {result.Output}");
        Assert.Contains("onboarding", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void PassingAnExistingFileInsteadOfADirectoryReportsItIsAFile()
    {
        string filePath = Path.Combine(_probe.Root, "somefile.txt");
        File.WriteAllText(filePath, "hello");

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done", filePath);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("is a file, not a directory", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingAnswerAgainstBlockOnboardingFailsGracefully()
    {
        string original = "ontology:\n  docs-root: docs\nonboarding:\n  nested: block\n";
        _probe.WriteHostConfig(original);

        ProbeResult result = _probe.RunCli("docs", "onboarding", "done");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid onboarding configuration", result.Output, StringComparison.Ordinal);
        Assert.Equal(original, ReadConfig());
    }

    private string ReadConfig()
    {
        return File.ReadAllText(Path.Combine(_probe.Root, ConfigRelativePath));
    }

    /// <summary>The configuration's own lines, trimmed, so a value is matched on its key and not on indentation.</summary>
    private IEnumerable<string> SettingLines()
    {
        return ReadConfig()
            .Split('\n')
            .Select(line => line.Trim())
            .ToArray();
    }
}
