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
        IsFileSystemCaseInsensitive: !OperatingSystem.IsLinux(),
        IsInputRedirected: Console.IsInputRedirected,
        KnownVariableName: "PATH",
        KnownVariableValue: Environment.GetEnvironmentVariable("PATH"),
        AbsentVariableName: AbsentVariable);

    private static OperatingSystemKind RunningFamily()
    {
        if (OperatingSystem.IsWindows())
        {
            return OperatingSystemKind.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OperatingSystemKind.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return OperatingSystemKind.Linux;
        }

        return OperatingSystem.IsFreeBSD() ? OperatingSystemKind.FreeBSD : OperatingSystemKind.Unknown;
    }
}

/// <summary>Runs the identifier contract against the generator over the operating system's sources.</summary>
public sealed class SystemIdGeneratorContractTests : IdGeneratorContractTests
{
    /// <inheritdoc />
    protected override IIdGenerator CreateGenerator() => new SystemIdGenerator();
}
