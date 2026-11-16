using KyberWeave.Application.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>The post-checkout hook's static entry point, for callers that have no container.</summary>
/// <remarks>The logic is <see cref="PostCheckoutHookInstaller"/> in the application layer.</remarks>
public static class PostCheckoutHook
{
    /// <summary>The check id the hook status reports under. Agents branch on it.</summary>
    public const string CheckId = PostCheckoutHookInstaller.CheckId;

    /// <inheritdoc cref="PostCheckoutHookInstaller.Inspect"/>
    public static DoctorCheck? Inspect(string repositoryRoot, IProcessExecutor executor) =>
        DoctorServices.PostCheckout.Inspect(repositoryRoot, executor);

    /// <inheritdoc cref="PostCheckoutHookInstaller.Ensure"/>
    public static DoctorCheck? Ensure(string repositoryRoot, IProcessExecutor executor) =>
        DoctorServices.PostCheckout.Ensure(repositoryRoot, executor);
}
