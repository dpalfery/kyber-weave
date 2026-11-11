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
///         <c>--ensure</c> prepares the index and then proves both tools, and is the one command a worker runs before
///         it starts work. A bare run provisions only when stdin is a terminal and <c>CI</c> is unset. Otherwise it
///         acts as <c>--check</c> and says so, so an unattended run never writes an index by surprise.
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

    /// <summary>How many of the mutually exclusive mode options the caller gave: check, provision, ensure and the hook's post-checkout mode.</summary>
    private static int ModesRequested(DoctorSettings settings) =>
        (settings.Check ? 1 : 0) + (settings.Provision ? 1 : 0) + (settings.Ensure ? 1 : 0) + (settings.PostCheckout ? 1 : 0);

    /// <inheritdoc />
    public override int Execute(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (ModesRequested(settings) > 1)
        {
            Console.Error.WriteLine("kyber-weave: error: choose one of --check, --provision, --ensure or --post-checkout, not more than one.");
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
        if (settings.PostCheckout)
            mode = DoctorMode.Ensure;
        else if (settings.Check)
            mode = DoctorMode.Check;
        else if (settings.Ensure)
            mode = DoctorMode.Ensure;
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

        string root;
        try
        {
            root = Path.GetFullPath(settings.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // The path is client input, and an unusable one is a refusal: exit 2, never an exception. Callers
            // branch on the 0/1/2/3 contract, so a stack trace would be indistinguishable from a broken doctor.
            Console.Error.WriteLine($"kyber-weave: error: '{settings.Path}' is not a usable path.");
            return 2;
        }

        IProcessExecutor executor = Executor ?? ProcessExecutor.WithTimeout(options.IndexTimeout);
        // The hook's own mode skips the MCP and Copilot probes and installs nothing, so it is routed apart from the doctor run.
        DoctorReport report = settings.PostCheckout
            ? RepositoryDoctor.RunPostCheckout(root, options, executor)
            : RepositoryDoctor.Run(root, mode, options, executor, environment);

        if (hint && !settings.Json)
            Console.Out.WriteLine(NonInteractiveHint);

        DoctorReportRenderer.Write(report, settings.Json);
        return report.ExitCode;
    }
}
