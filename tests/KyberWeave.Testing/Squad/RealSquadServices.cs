using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Testing.Squad;

/// <summary>The Squad file, path and launch services over the real disk and process environment, for a test that needs a tree on disk.</summary>
public static class RealSquadServices
{
    /// <summary>Creates the source loader over the real disk.</summary>
    /// <returns>The loader.</returns>
    public static SquadSourceLoader SourceLoader()
    {
        PhysicalFileSystem files = new();
        return new SquadSourceLoader(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Loads the Squad source under a root from the real disk.</summary>
    /// <param name="root">The Squad source root.</param>
    /// <returns>The loaded source.</returns>
    public static SquadSource Load(string root) => SourceLoader().Load(root);

    /// <summary>Creates the companion-skills lister over the real disk.</summary>
    /// <returns>The lister.</returns>
    public static SquadCompanionSkills CompanionSkills()
    {
        PhysicalFileSystem files = new();
        return new SquadCompanionSkills(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Creates the Squad source locator over the real disk.</summary>
    /// <returns>The locator.</returns>
    public static SquadPackSourceLocator SourceLocator()
    {
        PhysicalFileSystem files = new();
        return new SquadPackSourceLocator(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Creates the model preset validator over the real disk.</summary>
    /// <returns>The validator.</returns>
    public static SquadModelPresetValidator PresetValidator() => new(new PhysicalFileSystem());

    /// <summary>Creates the Copilot MCP server probe over the real disk.</summary>
    /// <returns>The probe.</returns>
    public static CopilotMcpServerProbe CopilotProbe() => new(new PhysicalFileSystem());

    /// <summary>Creates the operator channel overlay reader over the real disk.</summary>
    /// <returns>The reader.</returns>
    public static SquadOperatorChannelOverlayFile OperatorChannelOverlay() => new(new PhysicalFileSystem());

    /// <summary>Creates a worker baseline reader over the real disk, with a cache of its own.</summary>
    /// <returns>The reader.</returns>
    public static SquadWorkerBaselineFile WorkerBaseline()
    {
        PhysicalFileSystem files = new();
        return new SquadWorkerBaselineFile(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Creates the hook JSON file editor over the real disk.</summary>
    /// <returns>The editor.</returns>
    public static SquadHookJsonFile HookJsonFile()
    {
        PhysicalFileSystem files = new();
        return new SquadHookJsonFile(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Creates the worktree MCP binding helper over the real disk.</summary>
    /// <returns>The helper.</returns>
    public static SquadWorktreeMcp WorktreeMcp()
    {
        PhysicalFileSystem files = new();
        return new SquadWorktreeMcp(files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }

    /// <summary>Creates the render input reader over the real disk.</summary>
    /// <returns>The reader.</returns>
    public static FileSquadRenderInputReader RenderInputReader() => new(
        SourceLoader(),
        CopilotProbe(),
        OperatorChannelOverlay(),
        WorkerBaseline(),
        new PhysicalFileSystem(),
        new ProcessEnvironment());

    /// <summary>Creates the harness start-information builder over the real disk and process environment.</summary>
    /// <returns>The builder.</returns>
    public static SquadHarnessLauncher HarnessLauncher() => new(new PhysicalFileSystem(), new ProcessEnvironment());

    /// <summary>Creates the harness launcher that starts the real process.</summary>
    /// <returns>The launcher.</returns>
    public static ProcessSquadHarnessLauncher ProcessHarnessLauncher() => new(HarnessLauncher());

    /// <summary>Creates the instance link sync over the real disk, path resolver, mutex lease and environment.</summary>
    /// <returns>The sync.</returns>
    public static SquadInstanceLinkSync InstanceLinkSync()
    {
        PhysicalFileSystem files = new();
        return new SquadInstanceLinkSync(files, new PhysicalPathResolver(files), new MutexLease(), new ProcessEnvironment());
    }

    /// <summary>Creates the packager over the real disk.</summary>
    /// <param name="archives">Writes the archives.</param>
    /// <returns>The packager.</returns>
    public static FileSquadPackager Packager(ISquadArchiveWriter archives) =>
        new(archives, SourceLoader(), CompanionSkills());
}
