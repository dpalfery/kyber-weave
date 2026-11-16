using KyberWeave.Application.Doctor;
using KyberWeave.Core.Configuration;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Doctor;
using KyberWeave.Data.Host;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Core.Doctor;

/// <summary>
/// Builds the doctor services for the callers that have no container: the MCP host, the docs commands, and the tests
/// that call the static entry points below.
/// </summary>
/// <remarks>
/// A caller that holds the container takes <see cref="IRepositoryDoctor"/>, <see cref="IWorktreeIndexGuard"/> or
/// <see cref="ICodeGraphProvisioner"/> from it instead. Every object here is the one the container would build, over
/// the same adapters, so the two routes cannot diverge.
/// </remarks>
internal static class DoctorServices
{
    private static readonly IDoctorFileSystem Files = new PhysicalDoctorFileSystem();

    private static readonly IRepositoryToolSettings Settings = new ConfigFileToolSettings(KyberWeaveConfigLoader.Repository);

    internal static ICodeGraphProvisioner Provisioner { get; } =
        new CodeGraphIndexProvisioner(Files, new FileCodeGraphIndexWorkspace());

    internal static IWorktreeIndexGuard WorktreeIndex { get; } =
        new WorktreeIndexEnsurer(Provisioner, Files, Settings);

    internal static McpServerLiveProbe McpProbe { get; } = new(new StdioMcpSessionFactory());

    internal static PostCheckoutHookInstaller PostCheckout { get; } = new(new FileGitHookFiles(), Files, Settings);

    internal static IRepositoryDoctor Doctor { get; } = new RepositoryDoctorService(
        Provisioner,
        WorktreeIndex,
        McpProbe,
        PostCheckout,
        Files,
        Settings,
        new ProcessEnvironment());
}
