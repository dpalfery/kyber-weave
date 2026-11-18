using System.Runtime.InteropServices;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Host;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the environment contract against the real process, so the fake's answers cannot drift from it.</summary>
public sealed class PhysicalProcessEnvironmentContractTests : ProcessEnvironmentContractTests
{
    private static readonly string AbsentVariable = "KW_CONTRACT_ABSENT_" + Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    protected override IProcessEnvironment CreateEnvironment() => new ProcessEnvironment();

    /// <inheritdoc />
    protected override ProcessEnvironmentFacts Facts => new(
        CurrentDirectory: Environment.CurrentDirectory,
        HomeDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        TempDirectory: Path.GetTempPath(),
        UserName: Environment.UserName,
        MachineName: Environment.MachineName,
        OperatingSystem: RunningFamily(),
        IsFileSystemCaseInsensitive: ProbeCaseInsensitivity(),
        IsInputRedirected: Console.IsInputRedirected,
        KnownVariableName: "PATH",
        KnownVariableValue: Environment.GetEnvironmentVariable("PATH"),
        AbsentVariableName: AbsentVariable);

    private static OperatingSystemKind RunningFamily()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OperatingSystemKind.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return OperatingSystemKind.MacOS;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return OperatingSystemKind.Linux;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? OperatingSystemKind.FreeBSD : OperatingSystemKind.Unknown;
    }

    /// <summary>Asks the volume directly, with a technique independent of the adapter's.</summary>
    /// <returns>True when the temporary volume ignores case in names.</returns>
    private static bool ProbeCaseInsensitivity()
    {
        string directory = Path.GetTempPath();
        string name = "KW-CONTRACT-" + Guid.NewGuid().ToString("N") + "-Aa";
        string path = Path.Combine(directory, name);
        try
        {
            using (File.Create(path))
            {
            }

            return File.Exists(Path.Combine(directory, name.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Runs the identifier contract against the generator over the operating system's sources.</summary>
public sealed class SystemIdGeneratorContractTests : IdGeneratorContractTests
{
    /// <inheritdoc />
    protected override IIdGenerator CreateGenerator() => new SystemIdGenerator();
}
