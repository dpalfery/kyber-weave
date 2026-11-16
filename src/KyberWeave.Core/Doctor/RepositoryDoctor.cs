using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     The repository doctor's static entry point, for callers that have no container.
/// </summary>
/// <remarks>
///     The logic is <c>RepositoryDoctorService</c> in the application layer behind <see cref="IRepositoryDoctor"/>,
///     which the CLI resolves from its container. This forwards to the same service built over the real adapters.
/// </remarks>
public static class RepositoryDoctor
{
    /// <inheritdoc cref="IRepositoryDoctor.Run"/>
    public static DoctorReport Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        Func<string, string?> environment,
        string? lockDirectory = null) =>
        DoctorServices.Doctor.Run(root, mode, options, executor, environment, lockDirectory);

    /// <inheritdoc cref="IRepositoryDoctor.RunPostCheckout"/>
    public static DoctorReport RunPostCheckout(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string? lockDirectory = null) =>
        DoctorServices.Doctor.RunPostCheckout(root, options, executor, lockDirectory);
}
