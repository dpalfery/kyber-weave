using KyberWeave.Application;
using KyberWeave.Application.Doctor;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Kernel.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>The doctor services as the application registers them, for tests that drive them directly.</summary>
internal static class DoctorTestServices
{
    private static readonly ServiceProvider Provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

    /// <summary>Installs and inspects the git post-checkout hook.</summary>
    public static PostCheckoutHookInstaller PostCheckout { get; } = Provider.GetRequiredService<PostCheckoutHookInstaller>();

    /// <summary>The file-system seam the container registers, for a test that runs against the real disk.</summary>
    public static IDoctorFileSystem Files { get; } = Provider.GetRequiredService<IDoctorFileSystem>();

    /// <summary>Runs every doctor mode over the container's adapters.</summary>
    public static IRepositoryDoctor Doctor { get; } = Provider.GetRequiredService<IRepositoryDoctor>();

    /// <summary>Prepares the CodeGraph index over the container's adapters.</summary>
    public static ICodeGraphProvisioner Provisioner { get; } = Provider.GetRequiredService<ICodeGraphProvisioner>();

    /// <summary>Ensures a worktree's index over the container's adapters.</summary>
    public static IWorktreeIndexGuard WorktreeIndex { get; } = Provider.GetRequiredService<IWorktreeIndexGuard>();

    /// <summary>
    /// The <c>doctor</c> command over the container's real console, path and process adapters, with the process
    /// environment and children a test chooses.
    /// </summary>
    /// <param name="environment">Says whether input is a terminal and answers each variable.</param>
    /// <param name="executor">Runs the CodeGraph child; null for the bounded production runner.</param>
    /// <param name="mcpExecutable">The MCP executable to probe; null for the one on the path.</param>
    public static DoctorCommand Command(
        IProcessEnvironment environment,
        IProcessExecutor? executor = null,
        string? mcpExecutable = null) =>
        new(
            Doctor,
            environment,
            Provider.GetRequiredService<IConsole>(),
            Provider.GetRequiredService<IPathResolver>(),
            Provider.GetRequiredService<IBoundedProcessExecutor>(),
            executor,
            mcpExecutable);
}
