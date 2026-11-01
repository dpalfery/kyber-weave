using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Doctor;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Doctor;

/// <summary>
///     Checks the repository's documentation tooling: the CodeGraph index and the live docs MCP server.
/// </summary>
/// <remarks>
///     <para>
///         Mode resolution follows D11. <c>--check</c> is always read-only. <c>--provision</c> always provisions.
///         A bare run provisions only when stdin is a terminal and <c>CI</c> is unset. Otherwise it acts as
///         <c>--check</c> and says so, so an unattended run never writes an index by surprise.
///     </para>
///     <para>
///         The terminal and environment are injected, so the mode choice is testable without a real console. The
///         production defaults are the actual stdin and process environment.
///     </para>
/// </remarks>
public sealed class DoctorCommand : Command<DoctorSettings>
{
    /// <summary>The line a bare, non-interactive run prints so its user knows how to provision.</summary>
    internal const string NonInteractiveHint = "provisioning skipped: non-interactive; rerun with --provision";

    /// <summary>The executable name, resolved through PATH. Tests replace it so they never spawn the real server.</summary>
    internal const string DefaultMcpExecutable = "kyber-weave-mcp";

    /// <summary>The CodeGraph executable name, resolved through PATH.</summary>
    internal const string CodeGraphExecutable = "codegraph";

    /// <summary>Runs the CodeGraph child. Null uses a process executor bounded by the index timeout.</summary>
    internal IProcessExecutor? Executor { get; init; }

    /// <summary>Reports whether stdin is a terminal. Null uses the real stdin.</summary>
    internal Func<bool>? IsTerminal { get; init; }

    /// <summary>Reads one environment variable. Null uses the process environment.</summary>
    internal Func<string, string?>? ReadEnvironment { get; init; }

    /// <summary>The MCP executable. Null uses <see cref="DefaultMcpExecutable"/>.</summary>
    internal string? McpExecutable { get; init; }

    /// <inheritdoc />
    public override int Execute(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Check && settings.Provision)
        {
            Console.Error.WriteLine("kyber-weave: error: choose one of --check or --provision, not both.");
            return 2;
        }

        if (settings.IndexTimeoutSeconds <= 0 || settings.McpTimeoutSeconds <= 0)
        {
            Console.Error.WriteLine("kyber-weave: error: --index-timeout and --mcp-timeout must be positive seconds.");
            return 2;
        }

        Func<string, string?> environment = ReadEnvironment ?? Environment.GetEnvironmentVariable;
        bool terminal = (IsTerminal ?? (() => !Console.IsInputRedirected))();
        bool ci = !string.IsNullOrEmpty(environment("CI"));

        DoctorMode mode;
        bool hint = false;
        if (settings.Check)
            mode = DoctorMode.Check;
        else if (settings.Provision)
            mode = DoctorMode.Provision;
        else if (terminal && !ci)
            mode = DoctorMode.Provision;
        else
        {
            mode = DoctorMode.Check;
            hint = true;
        }

        DoctorOptions options = new(
            TimeSpan.FromSeconds(settings.IndexTimeoutSeconds),
            TimeSpan.FromSeconds(settings.McpTimeoutSeconds),
            CodeGraphExecutable,
            McpExecutable ?? DefaultMcpExecutable);

        IProcessExecutor executor = Executor ?? ProcessExecutor.WithTimeout(options.IndexTimeout);
        DoctorReport report = RepositoryDoctor.Run(
            Path.GetFullPath(settings.Path),
            mode,
            options,
            executor,
            environment);

        if (hint && !settings.Json)
            Console.Out.WriteLine(NonInteractiveHint);

        DoctorReportRenderer.Write(report, settings.Json);
        return report.ExitCode;
    }
}
