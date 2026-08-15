using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Checks the PATH-resolved Agent Package Manager version.</summary>
public sealed partial class ApmProcessProbe
{
    private readonly IProcessExecutor _executor;

    /// <summary>Creates an APM probe over an injectable process boundary.</summary>
    public ApmProcessProbe(IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
    }

    /// <summary>Runs only <c>apm --version</c> and parses its exact output envelope.</summary>
    public ToolProbeResult Probe() => ProcessProbe.Probe(
        _executor,
        "apm",
        ApmVersionRegex());

    [GeneratedRegex(
        "\\Aapm, version (?<version>" + SemanticVersionPattern.Value + ")(?:\\r?\\n)?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ApmVersionRegex();
}

/// <summary>Checks the PATH-resolved Kyber-Weave MCP version.</summary>
public sealed partial class McpProcessProbe
{
    private readonly IProcessExecutor _executor;

    /// <summary>Creates an MCP probe over an injectable process boundary.</summary>
    public McpProcessProbe(IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
    }

    /// <summary>Runs only <c>kyber-weave-mcp --version</c> and parses its exact output envelope.</summary>
    public ToolProbeResult Probe() => ProcessProbe.Probe(
        _executor,
        "kyber-weave-mcp",
        McpVersionRegex());

    [GeneratedRegex(
        "\\Akyber-weave-mcp (?<version>" + SemanticVersionPattern.Value + ")(?:\\r?\\n)?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex McpVersionRegex();
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
        string executable,
        Regex outputPattern)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executable);
        ProcessResult result;
        try
        {
            result = executor.Run(startInfo, string.Empty);
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

    private static ProcessStartInfo CreateStartInfo(string executable)
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
        CopyEnvironmentVariable(startInfo, "PATH");
        if (OperatingSystem.IsWindows())
        {
            CopyEnvironmentVariable(startInfo, "PATHEXT");
            CopyEnvironmentVariable(startInfo, "SystemRoot");
            CopyEnvironmentVariable(startInfo, "WINDIR");
        }

        return startInfo;
    }

    private static void CopyEnvironmentVariable(ProcessStartInfo startInfo, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            startInfo.Environment[name] = value;
    }
}
