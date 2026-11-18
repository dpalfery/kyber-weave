using System.Collections;
using System.Runtime.InteropServices;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Host;
using KyberWeave.Tests.Host;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the environment contract against the real process, so the fake's answers cannot drift from it.</summary>
public sealed class PhysicalProcessEnvironmentContractTests : ProcessEnvironmentContractTests
{
    private static readonly (string Name, string Value) KnownVariable = FirstSetVariable();
    private static readonly string AbsentVariable = AbsentName();

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
        KnownVariableName: KnownVariable.Name,
        KnownVariableValue: KnownVariable.Value,
        AbsentVariableName: AbsentVariable);

    /// <summary>Names a variable the process really has, so the read test compares two values rather than two absences.</summary>
    /// <returns>The first set variable's name and value.</returns>
    private static (string Name, string Value) FirstSetVariable()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value && value.Length > 0)
            {
                return (name, value);
            }
        }

        throw new InvalidOperationException("The contract needs one environment variable with a value.");
    }

    /// <summary>Names a variable this process does not have, so the absent test proves a real absence.</summary>
    /// <returns>A variable name that is not set.</returns>
    private static string AbsentName()
    {
        while (true)
        {
            string name = "KW_CONTRACT_ABSENT_" + Guid.NewGuid().ToString("N");
            if (Environment.GetEnvironmentVariable(name) is null)
            {
                return name;
            }
        }
    }

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
