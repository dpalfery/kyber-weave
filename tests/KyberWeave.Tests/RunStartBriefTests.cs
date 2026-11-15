using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     <c>run start --brief</c>: the conductor's brief appended to the run's own facts, with the report and exit
///     codes unchanged and a brief that cannot be built never failing a run whose branch is already in place.
/// </summary>
public sealed class RunStartBriefTests : IDisposable
{
    private static readonly string[] Common = ["--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate"];

    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose() => _probe.Dispose();

    /// <summary>Installs the conductor's real reference files at the project-scope location for Claude.</summary>
    private void InstallConductorReferences()
    {
        string source = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents", "conductor", "references");
        string target = Path.Combine(_probe.Root, ".claude", "agents", "conductor", "references");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    [Fact]
    public void AppendsOnlyTheBriefForThePathAndAutonomyGiven()
    {
        InstallConductorReferences();

        ProbeResult result = _probe.RunStart([.. Common, "--brief", "--path", "plan", "--autonomy", "supervised"]);

        Assert.True(result.ExitCode == 0, result.Output);
        // The header's command name is bold where the host reports colour support (CI does), so the styling
        // codes are removed before the text is compared.
        string output = Regex.Replace(result.StandardOutput, "\u001b\\[[0-9;]*m", string.Empty);
        Assert.Contains("run start ready", output, StringComparison.Ordinal);
        Assert.Contains("BRIEF conductor | facts: path=plan (given), autonomy=supervised (given)", output, StringComparison.Ordinal);
        Assert.Contains("=== plan-path.md ===", output, StringComparison.Ordinal);
        Assert.Contains("=== autonomy.md ===", output, StringComparison.Ordinal);
        Assert.DoesNotContain("=== intake-path.md ===", output, StringComparison.Ordinal);
        Assert.DoesNotContain("=== spec-path.md ===", output, StringComparison.Ordinal);
        Assert.DoesNotContain("## Hard stops", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomousKeepsTheAutonomousSections()
    {
        InstallConductorReferences();

        ProbeResult result = _probe.RunStart([.. Common, "--brief", "--path", "intake", "--autonomy", "autonomous"]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("=== intake-path.md ===", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("## Hard stops", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBriefTheOutputIsUnchanged()
    {
        InstallConductorReferences();

        ProbeResult result = _probe.RunStart([.. Common]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain("BRIEF", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCarriesTheBriefAsAFieldOnlyWhenAsked()
    {
        InstallConductorReferences();

        ProbeResult without = _probe.RunStart([.. Common, "--json"]);
        ProbeResult with = _probe.RunStart(["--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate-two", "--json", "--brief", "--path", "spec", "--autonomy", "supervised"]);

        using JsonDocument plain = JsonDocument.Parse(without.StandardOutput);
        Assert.False(plain.RootElement.TryGetProperty("brief", out _));
        using JsonDocument document = JsonDocument.Parse(with.StandardOutput);
        JsonElement brief = document.RootElement.GetProperty("brief");
        Assert.Equal("conductor", brief.GetProperty("agent").GetString());
        Assert.True(brief.GetProperty("characters").GetProperty("brief").GetInt32()
            < brief.GetProperty("characters").GetProperty("full").GetInt32());
        Assert.Equal("tighten-the-gate-two", document.RootElement.GetProperty("branch").GetString()!.Split('/')[2]);
    }

    [Fact]
    public void ABriefThatCannotBeBuiltIsAWarningAndTheRunStillSucceeds()
    {
        ProbeResult result = _probe.RunStart([.. Common, "--brief", "--path", "plan"]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("the conductor's brief could not be built", result.Output, StringComparison.Ordinal);
        Assert.Contains("read its reference files whole", result.Output, StringComparison.Ordinal);
        Assert.True(_probe.BranchExists("claude/fix/tighten-the-gate"));
    }

    [Theory]
    [InlineData("--path", "sideways")]
    [InlineData("--autonomy", "reckless")]
    public void AFactOutsideTheVocabularyIsRefusedBeforeAnyBranchIsMade(string option, string value)
    {
        ProbeResult result = _probe.RunStart([.. Common, "--brief", option, value]);

        Assert.Equal(2, result.ExitCode);
        Assert.False(_probe.BranchExists("claude/fix/tighten-the-gate"));
    }

    [Fact]
    public void PathWithoutBriefIsRefused()
    {
        ProbeResult result = _probe.RunStart([.. Common, "--path", "plan"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("only apply with --brief", result.Output, StringComparison.Ordinal);
    }
}
