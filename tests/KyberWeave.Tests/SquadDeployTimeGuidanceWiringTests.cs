using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Harness = KyberWeave.Tests.Fakes.SquadHomeHarness;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the deploy-time response-guidance wiring contract: the user-home guidance becomes the
///     effective response-preferences text on every installed entry file, survives updates, and is
///     created as part of global install while preserving local edits and the existing home-file
///     protection rules.
/// </summary>
public sealed class SquadDeployTimeGuidanceWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public async Task InstallGlobalInjectsTheEffectiveGuidanceIntoEveryEntryFile()
    {
        using Harness h = NewHarness("install-global-guidance");
        string homeText = "Use short tool calls and be explicit about assumptions.";
        WriteHomeGuidance(h, homeText);

        SquadLifecycleResult result = await h.Lifecycle(SquadCommandComposition.ResolveRenderer())
            .InstallAsync(new SquadInstallRequest(
                h.MakeDirectory("anchor"),
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.3"));

        Assert.True(result.Success, DescribeFailure(result));
        string[] files = InstalledEntryFiles(h, SquadTarget.Codex);
        Assert.NotEmpty(files);
        foreach (string path in files)
        {
            string content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Assert.Contains($"<response-preferences>{homeText}</response-preferences>", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpdateGlobalRefreshesEntryFilesAndKeepsTheCurrentHomeEdit()
    {
        using Harness h = NewHarness("update-guidance");
        string original = "Keep every answer under three sentences.";
        string updated = "Keep every answer under two sentences and ask one clarifying question.";
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, original);

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        SquadLifecycleResult install = await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        WriteHomeGuidance(h, updated);
        SquadLifecycleResult refresh = await lifecycle.UpdateAsync(new SquadUpdateRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.4"));

        Assert.True(refresh.Success, DescribeFailure(refresh));
        string[] files = InstalledEntryFiles(h, SquadTarget.Codex);
        Assert.NotEmpty(files);
        foreach (string path in files)
        {
            string content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Assert.Contains($"<response-preferences>{updated}</response-preferences>", content, StringComparison.Ordinal);
        }

        string homePath = GuidancePath(h);
        Assert.True(File.Exists(homePath), "The home response guidance file must be preserved through update.");
        Assert.Equal(updated, await File.ReadAllTextAsync(homePath, Encoding.UTF8));
    }

    [Fact]
    public async Task UpdatePreservesAHumanEditedConductorEntryAndReportsIt()
    {
        using Harness h = NewHarness("preserve-local-edit");
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, "Version 1 guidance.");

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        Assert.True((await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"))).Success);

        string conductor = Path.Combine(h.GlobalRoot(SquadTarget.Codex), "agents", "conductor.toml");
        string original = await File.ReadAllTextAsync(conductor, Encoding.UTF8);
        string localEdit = original + "\n# operator edit\n";
        await File.WriteAllTextAsync(conductor, localEdit, Encoding.UTF8);

        WriteHomeGuidance(h, "Version 2 guidance.");
        SquadLifecycleResult update = await lifecycle.UpdateAsync(new SquadUpdateRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.4"));

        Assert.True(update.Success, DescribeFailure(update));
        string persisted = await File.ReadAllTextAsync(conductor, Encoding.UTF8);
        Assert.Equal(localEdit, persisted);
        Assert.Contains("Kept the edited", string.Join(Environment.NewLine, update.Warnings ?? []), StringComparison.Ordinal);
        Assert.Contains("conductor", string.Join(Environment.NewLine, update.Warnings ?? []), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateWithAnUnreadableDeployedEntryDegradesToAWarningAndSucceeds()
    {
        using Harness h = NewHarness("unreadable-deployed-entry");
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, "Version 1 guidance.");

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        Assert.True((await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"))).Success);

        string conductor = Path.Combine(h.GlobalRoot(SquadTarget.Codex), "agents", "conductor.toml");
        SquadLifecycleResult update;
        using (Harness.DenyReadOrSkip(conductor))
        {
            // The preserved-edit report hashes every receipt-owned file; an unreadable one must become a
            // warning naming it, not an exception that aborts the whole update.
            update = await lifecycle.UpdateAsync(new SquadUpdateRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.4"));
        }

        Assert.True(update.Success, DescribeFailure(update));
        Assert.Contains("conductor", string.Join(Environment.NewLine, update.Warnings ?? []), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateSurfacesAnUnreadableDeployedEntryPathInWarnings()
    {
        using Harness h = NewHarness("unreadable-entry-warning");
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, "Version 1 guidance.");

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        Assert.True((await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"))).Success);

        string conductor = Path.Combine(h.GlobalRoot(SquadTarget.Codex), "agents", "conductor.toml");
        SquadLifecycleResult update;
        using (Harness.DenyReadOrSkip(conductor))
        {
            update = await lifecycle.UpdateAsync(new SquadUpdateRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.4"));
        }

        Assert.True(update.Success, DescribeFailure(update));
        // The degraded path is named in the warnings the CLI prints, not only in the plan.
        Assert.Contains("conductor.toml", string.Join(Environment.NewLine, update.Warnings ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunUpdateSurfacesAnUnreadableDeployedEntryPathInWarnings()
    {
        using Harness h = NewHarness("unreadable-entry-dry-run");
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, "Version 1 guidance.");

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        Assert.True((await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"))).Success);

        string conductor = Path.Combine(h.GlobalRoot(SquadTarget.Codex), "agents", "conductor.toml");
        SquadLifecycleResult dryRun;
        using (Harness.DenyReadOrSkip(conductor))
        {
            dryRun = await lifecycle.UpdateAsync(new SquadUpdateRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.4",
                DryRun: true));
        }

        Assert.True(dryRun.Success, DescribeFailure(dryRun));
        Assert.True(dryRun.DryRun);
        Assert.Contains("conductor.toml", string.Join(Environment.NewLine, dryRun.Warnings ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorParityMatchesDeployedBytesAfterInjection()
    {
        using Harness h = NewHarness("doctor-parity");
        string anchor = h.MakeDirectory("anchor");
        WriteHomeGuidance(h, "Doctor parity is the operator-level default.");

        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        SquadLifecycleResult install = await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        foreach (string path in InstalledEntryFiles(h, SquadTarget.Codex))
        {
            string actual = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Assert.Contains("<response-preferences>Doctor parity is the operator-level default.</response-preferences>", actual, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProjectScopeInstallInjectsGuidanceIntoEveryEntryFileAcrossAllTargets()
    {
        using Harness h = NewHarness("project-scope-guidance");
        string projectRoot = h.MakeDirectory("project");
        string homeText = "Every target must carry the same operator guidance.";
        WriteHomeGuidance(h, homeText);

        SquadLifecycleResult result = await h.Lifecycle(SquadCommandComposition.ResolveRenderer())
            .InstallAsync(new SquadInstallRequest(
                projectRoot,
                SquadDeploymentScope.Project,
                SquadTargetCatalog.All,
                Version: "1.2.3"));

        Assert.True(result.Success, DescribeFailure(result));

        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string[] files = InstalledEntryFiles(h, target, projectRoot);
            Assert.NotEmpty(files);
            foreach (string path in files)
            {
                string content = await File.ReadAllTextAsync(path, Encoding.UTF8);
                Assert.Contains($"<response-preferences>{homeText}</response-preferences>", content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task UnreadableHomeFileFallsBackToTheShippedDefaultAndWarns()
    {
        using Harness h = NewHarness("unreadable-home-guidance");
        string anchor = h.MakeDirectory("anchor");
        string path = GuidancePath(h);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(path);

        SquadLifecycleResult result = await h.Lifecycle(SquadCommandComposition.ResolveRenderer())
            .InstallAsync(new SquadInstallRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.3"));

        Assert.True(result.Success, DescribeFailure(result));
        Assert.NotNull(result.Warnings);
        Assert.Contains("response-guidance", string.Join(Environment.NewLine, result.Warnings), StringComparison.OrdinalIgnoreCase);

        foreach (string entry in InstalledEntryFiles(h, SquadTarget.Codex))
        {
            string content = await File.ReadAllTextAsync(entry, Encoding.UTF8);
            Assert.Contains($"<response-preferences>{SquadResponseGuidanceSource.DefaultText}</response-preferences>", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GlobalInstallCreatesTheHomeGuidanceFileWithTheShippedDefault()
    {
        using Harness h = NewHarness("install-creates-home-guidance");
        string anchor = h.MakeDirectory("anchor");
        string guidancePath = GuidancePath(h);
        Assert.False(File.Exists(guidancePath), "The scenario requires the home guidance file to be absent before install.");

        SquadLifecycleResult result = await h.Lifecycle(SquadCommandComposition.ResolveRenderer())
            .InstallAsync(new SquadInstallRequest(
                anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                Version: "1.2.3"));

        Assert.True(result.Success, DescribeFailure(result));
        Assert.True(File.Exists(guidancePath), "Global install must create ~/.kyber-weave/response-guidance.md.");
        byte[] bytes = await File.ReadAllBytesAsync(guidancePath);
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, Encoding.UTF8.GetString(bytes));
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(Encoding.UTF8.GetBytes(SquadResponseGuidanceSource.DefaultText)));
    }

    [Fact]
    public async Task UpdateNeverOverwritesAHandEditedHomeGuidanceFile()
    {
        using Harness h = NewHarness("update-keeps-edited-home-guidance");
        string anchor = h.MakeDirectory("anchor");
        string guidancePath = GuidancePath(h);
        string original = "Operator edit that update must never overwrite.";

        WriteHomeGuidance(h, original);
        SquadLifecycleService lifecycle = h.Lifecycle(SquadCommandComposition.ResolveRenderer());
        Assert.True((await lifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.3"))).Success);

        string changed = "Updated operator guidance that must persist across update.";
        await File.WriteAllTextAsync(guidancePath, changed, Encoding.UTF8);
        SquadLifecycleResult result = await lifecycle.UpdateAsync(new SquadUpdateRequest(
            anchor,
            SquadDeploymentScope.Global,
            [SquadTarget.Codex],
            Version: "1.2.4"));

        Assert.True(result.Success, DescribeFailure(result));
        Assert.Equal(changed, await File.ReadAllTextAsync(guidancePath, Encoding.UTF8));
        Assert.Contains("Kept the edited response guidance", string.Join(Environment.NewLine, result.Warnings ?? []), StringComparison.Ordinal);
    }

    private static string GuidancePath(Harness h)
    {
        return Path.Combine(h.Home, ".kyber-weave", "response-guidance.md");
    }

    private static void WriteHomeGuidance(Harness h, string text)
    {
        string path = GuidancePath(h);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static string[] InstalledEntryFiles(Harness h, SquadTarget target, string? projectRoot = null)
    {
        string root = projectRoot is null ? h.GlobalRoot(target) : projectRoot;

        // The global Codex home is $CODEX_HOME itself, so the renderer's ".codex/" prefix is not part of
        // the installed path there. A project root keeps the prefix. The renderer decides both layouts.
        bool globalCodex = projectRoot is null && target == SquadTarget.Codex;
        string[] expected = TargetEntryFiles(target)
            .Select(relativePath => globalCodex && relativePath.StartsWith(".codex/", StringComparison.Ordinal)
                ? relativePath[".codex/".Length..]
                : relativePath)
            .Select(relativePath => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToArray();

        return expected;
    }

    private static IReadOnlyList<string> TargetEntryFiles(SquadTarget target)
    {
        return target switch
        {
            SquadTarget.Codex => [
                ".codex/agents/conductor.toml",
                ".codex/skills/conductor/SKILL.md",
                ".codex/agents/bug-crusher.toml",
                ".codex/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Cursor => [
                ".cursor/skills/conductor/SKILL.md",
                ".cursor/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Claude => [
                ".claude/agents/conductor.md",
                ".claude/skills/conductor/SKILL.md",
                ".claude/agents/bug-crusher.md",
                ".claude/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Copilot => [
                ".github/agents/conductor.agent.md",
                ".github/agents/bug-crusher.agent.md",
                ".github/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.OpenCode => [
                ".opencode/agents/conductor.md",
                ".opencode/agents/bug-crusher.md",
                ".opencode/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Kilo => [
                ".kilo/agents/conductor.md",
                ".kilo/agents/bug-crusher.md",
                ".kilo/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Antigravity => [
                ".agents/agents/conductor/agent.md",
                ".agents/agents/bug-crusher/agent.md",
                ".agents/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Factory => [
                ".factory/droids/conductor.md",
                ".factory/skills/conductor/SKILL.md",
                ".factory/droids/bug-crusher.md",
                ".factory/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Warp => [
                ".warp/skills/conductor/SKILL.md",
                ".warp/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Pi => [
                ".pi/skills/conductor/SKILL.md",
                ".pi/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.ZCode => [
                ".zcode/commands/conductor.md",
                ".zcode/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.Devin => [
                ".devin/skills/conductor/SKILL.md",
                ".devin/skills/bug-crusher/SKILL.md"
            ],
            SquadTarget.CommandCode => [
                ".commandcode/agents/conductor.md",
                ".commandcode/skills/conductor/SKILL.md",
                ".commandcode/agents/bug-crusher.md",
                ".commandcode/skills/bug-crusher/SKILL.md"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unexpected target.")
        };
    }

    private static string DescribeFailure(SquadLifecycleResult result)
    {
        string details = result.Errors is { Count: > 0 }
            ? string.Join(Environment.NewLine, result.Errors)
            : result.Warnings is { Count: > 0 }
                ? string.Join(Environment.NewLine, result.Warnings)
                : "No diagnosis was reported.";
        return details;
    }

    private Harness NewHarness(string name)
    {
        return new Harness(Path.Combine(_temp.Path, name));
    }
}
