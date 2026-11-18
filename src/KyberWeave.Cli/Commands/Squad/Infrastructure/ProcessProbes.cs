using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Cli;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Checks the PATH-resolved Kyber-Weave MCP version.</summary>
public sealed partial class McpProcessProbe
{
    private readonly IProcessExecutor _executor;
    private readonly IProcessEnvironment _environment;

    /// <summary>Creates an MCP probe over an injectable process boundary.</summary>
    /// <param name="executor">Runs the probe.</param>
    /// <param name="environment">Names the variables the probe's child needs to start.</param>
    public McpProcessProbe(IProcessExecutor executor, IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(environment);
        _executor = executor;
        _environment = environment;
    }

    /// <summary>Runs only <c>kyber-weave-mcp --version</c> and parses its exact output envelope.</summary>
    public ToolProbeResult Probe() => ProcessProbe.Probe(
        _executor,
        _environment,
        "kyber-weave-mcp",
        McpVersionRegex());

    [GeneratedRegex(
        "\\Akyber-weave-mcp (?<version>" + SemanticVersionPattern.Value + ")(?:\\r?\\n)?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex McpVersionRegex();
}

/// <summary>Checks the PATH-resolved Kyber-Weave Arbiter version.</summary>
/// <remarks>
/// Follows <see cref="McpProcessProbe"/>: runs only <c>kyber-weave-arbiter --version</c>
/// and parses its exact output envelope.
/// </remarks>
public sealed partial class ArbiterProcessProbe : IArbiterBinaryProbe
{
    private readonly IProcessExecutor _executor;
    private readonly IProcessEnvironment _environment;

    /// <inheritdoc />
    public string CliVersion => CliAssemblyVersion.ForProbe(typeof(ArbiterProcessProbe).Assembly);

    /// <summary>Creates an Arbiter probe over an injectable process boundary.</summary>
    /// <param name="executor">Runs the probe.</param>
    /// <param name="environment">Names the variables the probe's child needs to start.</param>
    public ArbiterProcessProbe(IProcessExecutor executor, IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(environment);
        _executor = executor;
        _environment = environment;
    }

    /// <summary>Runs only <c>kyber-weave-arbiter --version</c> and parses its exact output envelope.</summary>
    public ToolProbeResult Probe() => ProcessProbe.Probe(
        _executor,
        _environment,
        "kyber-weave-arbiter",
        ArbiterVersionRegex());

    [GeneratedRegex(
        "\\Akyber-weave-arbiter (?<version>" + SemanticVersionPattern.Value + ")(?:\\r?\\n)?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArbiterVersionRegex();
}

internal static class SemanticVersionPattern
{
    // Numeric prerelease identifiers disallow leading zeroes; identifiers containing a
    // letter or hyphen use the other branch. Build identifiers have no leading-zero rule.
    public const string Value =
        "(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)" +
        "(?:-(?:(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)" +
        "(?:\\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?";
}

internal static class ProcessProbe
{
    public static ToolProbeResult Probe(
        IProcessExecutor executor,
        IProcessEnvironment environment,
        string executable,
        Regex outputPattern)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable, environment);
        ProcessResult result;
        try
        {
            result = executor.Run(startInfo, string.Empty);
        }
        catch (TimeoutException)
        {
            // The tool started but stopped answering. The executor has already killed it, so name the
            // hang rather than reporting a missing tool.
            return new ToolProbeResult(
                IsAvailable: true,
                Version: null,
                FailureReason: $"The '{executable} --version' probe did not finish in time and was stopped.");
        }
        catch (Exception)
        {
            // Startup exceptions can echo PATH values or launch credentials. The probe
            // reports only the category needed by doctor/status callers.
            return new ToolProbeResult(
                IsAvailable: false,
                Version: null,
                FailureReason: $"The '{executable}' executable is not available on PATH.");
        }

        if (result.ExitCode != 0)
        {
            // Neither standard stream is reflected: command failures frequently include
            // environment values, authorization headers, or provider diagnostics.
            return new ToolProbeResult(
                IsAvailable: true,
                Version: null,
                FailureReason: $"The '{executable} --version' probe failed.");
        }

        const int MaximumVersionOutputLength = 256;
        if (result.StandardOutput.Length > MaximumVersionOutputLength)
        {
            return new ToolProbeResult(
                IsAvailable: true,
                Version: null,
                FailureReason: $"The '{executable} --version' output was not recognized.");
        }

        Match match = outputPattern.Match(result.StandardOutput);
        if (!match.Success)
        {
            return new ToolProbeResult(
                IsAvailable: true,
                Version: null,
                FailureReason: $"The '{executable} --version' output was not recognized.");
        }

        return new ToolProbeResult(
            IsAvailable: true,
            Version: match.Groups["version"].Value,
            FailureReason: null);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IProcessEnvironment environment)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        // Version checks do not need provider credentials. Start from an empty child
        // environment and restore only values needed to locate and start a PATH tool.
        startInfo.Environment.Clear();
        CopyEnvironmentVariable(startInfo, environment, "PATH");
        if (environment.OperatingSystem == OperatingSystemKind.Windows)
        {
            CopyEnvironmentVariable(startInfo, environment, "PATHEXT");
            CopyEnvironmentVariable(startInfo, environment, "SystemRoot");
            CopyEnvironmentVariable(startInfo, environment, "WINDIR");
        }

        return startInfo;
    }

    private static void CopyEnvironmentVariable(ProcessStartInfo startInfo, IProcessEnvironment environment, string name)
    {
        string? value = environment.GetVariable(name);
        if (!string.IsNullOrEmpty(value))
            startInfo.Environment[name] = value;
    }
}
