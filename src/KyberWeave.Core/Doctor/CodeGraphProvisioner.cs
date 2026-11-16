using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Core.Doctor;

/// <summary>The CodeGraph index preparation's static entry point, for callers that have no container.</summary>
/// <remarks>
///     The logic is <c>CodeGraphIndexProvisioner</c> in the application layer behind
///     <see cref="Contracts.Services.ICodeGraphProvisioner"/>; the pure rules are <see cref="CodeGraphIndexRules"/>.
/// </remarks>
public static class CodeGraphProvisioner
{
    /// <summary>The tracked files <c>codegraph init</c> writes; see <see cref="CodeGraphIndexRules.InitConfigFiles"/>.</summary>
    internal static IReadOnlyList<string> InitConfigFiles => CodeGraphIndexRules.InitConfigFiles;

    /// <inheritdoc cref="CodeGraphIndexRules.LockPathFor"/>
    public static string LockPathFor(string root, string lockDirectory) =>
        CodeGraphIndexRules.LockPathFor(root, lockDirectory);

    /// <inheritdoc cref="CodeGraphIndexRules.IsBusy"/>
    public static bool IsBusy(DoctorCheck check) => CodeGraphIndexRules.IsBusy(check);

    /// <inheritdoc cref="CodeGraphIndexRules.IsSharingViolation"/>
    public static bool IsSharingViolation(IOException ex) => CodeGraphIndexRules.IsSharingViolation(ex);

    /// <inheritdoc cref="CodeGraphIndexRules.QuoteShellPath"/>
    internal static string QuoteShellPath(string path, bool isWindows) =>
        CodeGraphIndexRules.QuoteShellPath(path, isWindows);

    /// <inheritdoc cref="Contracts.Services.ICodeGraphProvisioner.Provision"/>
    public static DoctorCheck Provision(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        TimeSpan lockWait) =>
        DoctorServices.Provisioner.Provision(root, options, executor, lockDirectory, lockWait);
}
