using KyberWeave.Application;
using KyberWeave.Application.Doctor;
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
}
